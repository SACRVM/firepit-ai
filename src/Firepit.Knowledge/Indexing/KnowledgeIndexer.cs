using System.Security.Cryptography;
using System.Text;
using Firepit.Knowledge.Embeddings;
using Firepit.Knowledge.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Firepit.Knowledge.Indexing;

// Incremental indexer for one knowledge scope. The `documents` table is the
// manifest: a file whose SHA-256 matches its row is skipped, so a full
// rescan of an unchanged scope is one directory walk plus hashing. Files are
// chunked and FTS-indexed even while the embedding model is still
// downloading — those docs stay `embedded = 0` and are picked up by the next
// pass once the model is ready.
public sealed class KnowledgeIndexer
{
    private readonly KnowledgeStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger _logger;

    public KnowledgeIndexer(KnowledgeStore store, IEmbeddingService embeddings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embeddings);
        _store = store;
        _embeddings = embeddings;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IndexStats> ReindexAsync(CancellationToken ct = default)
    {
        var dirExists = Directory.Exists(_store.KnowledgeDir);

        // Nothing on disk and no index yet: stay inert. Opening a connection
        // here would create `.firepit/knowledge.db` in projects that never
        // opted into knowledge.
        if (!dirExists && !_store.IndexExists)
        {
            return IndexStats.Empty;
        }

        var files = dirExists
            ? Directory.EnumerateFiles(_store.KnowledgeDir, "*.md", SearchOption.AllDirectories).ToList()
            : [];

        using var conn = _store.OpenConnection();
        var existing = ReadManifest(conn);

        var indexed = 0;
        var unchanged = 0;
        var removed = 0;
        var pending = 0;
        var skipped = 0;
        var embeddingsAvailable = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var rel = Path.GetRelativePath(_store.KnowledgeDir, file).Replace('\\', '/');
            seen.Add(rel);

            byte[] bytes;
            try
            {
                // Non-blocking: an index pass must never make a save fail.
                bytes = await Store.NonBlockingFile.ReadAllBytesAsync(file, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mid-write, locked by an editor, or gone since the listing.
                // Counted, not just logged: the pass is now incomplete, and the
                // caller has to know that so it schedules another one. Left as
                // a debug log only, this document would stay unfindable for as
                // long as nothing else happened to touch it.
                skipped++;
                _logger.LogDebug(ex, "Skipping unreadable knowledge file {Path}", rel);
                continue;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (existing.TryGetValue(rel, out var row) && row.Hash == hash)
            {
                // Re-process an unchanged doc only to backfill vectors the
                // model wasn't ready for last time.
                if (row.Embedded || !embeddingsAvailable)
                {
                    unchanged++;
                    if (!row.Embedded)
                    {
                        pending++;
                    }

                    continue;
                }
            }

            var text = DecodeUtf8(bytes);
            var (title, chunks) = MarkdownChunker.Chunk(Path.GetFileName(file), text);

            // Embed outside the write transaction — ~50ms per chunk of CPU
            // has no business holding a write lock.
            var vectors = new float[]?[chunks.Count];
            if (embeddingsAvailable)
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    try
                    {
                        vectors[i] = await _embeddings.EmbedAsync(
                            BuildEmbeddingText(title, chunks[i]), ct);
                    }
                    catch (EmbeddingUnavailableException)
                    {
                        embeddingsAvailable = false;
                        break;
                    }
                }
            }

            var embedded = embeddingsAvailable;
            WriteDocument(conn, rel, title, hash, chunks, vectors, embedded);
            indexed++;
            if (!embedded)
            {
                pending++;
            }
        }

        foreach (var gone in existing.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            DeleteDocument(conn, gone);
            removed++;
        }

        return new IndexStats(indexed, unchanged, removed, pending, skipped);
    }

    private static string BuildEmbeddingText(string title, MarkdownChunk chunk)
    {
        // Prefix the doc title and section heading so short chunks carry
        // their context into the vector — mirrors fishbowl's
        // "title + content + tags" recipe.
        return $"{title} {chunk.Heading} {chunk.Content}".Trim();
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;
    }

    private static Dictionary<string, (string Hash, bool Embedded)> ReadManifest(SqliteConnection conn)
    {
        var result = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, content_hash, embedded FROM documents";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2) != 0);
        }

        return result;
    }

    private static void WriteDocument(
        SqliteConnection conn,
        string relPath,
        string title,
        string hash,
        IReadOnlyList<MarkdownChunk> chunks,
        float[]?[] vectors,
        bool embedded)
    {
        using var tx = conn.BeginTransaction();

        DeleteDocumentRows(conn, tx, relPath);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var chunkId = $"{relPath}#{chunk.Ordinal}";

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO chunks(chunk_id, doc_path, heading, content, ord) " +
                    "VALUES(@id, @path, @heading, @content, @ord)";
                insert.Parameters.AddWithValue("@id", chunkId);
                insert.Parameters.AddWithValue("@path", relPath);
                insert.Parameters.AddWithValue("@heading", (object?)chunk.Heading ?? DBNull.Value);
                insert.Parameters.AddWithValue("@content", chunk.Content);
                insert.Parameters.AddWithValue("@ord", chunk.Ordinal);
                insert.ExecuteNonQuery();
            }

            long rowid;
            using (var last = conn.CreateCommand())
            {
                last.Transaction = tx;
                last.CommandText = "SELECT last_insert_rowid()";
                rowid = (long)last.ExecuteScalar()!;
            }

            using (var fts = conn.CreateCommand())
            {
                fts.Transaction = tx;
                fts.CommandText =
                    "INSERT INTO chunks_fts(rowid, heading, content) VALUES(@rowid, @heading, @content)";
                fts.Parameters.AddWithValue("@rowid", rowid);
                fts.Parameters.AddWithValue("@heading", chunk.Heading ?? string.Empty);
                fts.Parameters.AddWithValue("@content", chunk.Content);
                fts.ExecuteNonQuery();
            }

            if (vectors[i] is { } vec)
            {
                // sqlite-vec vec0 FLOAT[N] stores 4*N bytes little-endian.
                // .NET's float memory layout is IEEE 754 little-endian on
                // every platform we support.
                var blob = new byte[vec.Length * sizeof(float)];
                Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);

                using var vecInsert = conn.CreateCommand();
                vecInsert.Transaction = tx;
                vecInsert.CommandText = "INSERT INTO vec_chunks(id, embedding) VALUES(@id, @blob)";
                vecInsert.Parameters.AddWithValue("@id", chunkId);
                vecInsert.Parameters.AddWithValue("@blob", blob);
                vecInsert.ExecuteNonQuery();
            }
        }

        using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO documents(path, title, content_hash, indexed_at, embedded)
                VALUES(@path, @title, @hash, @at, @embedded)
                ON CONFLICT(path) DO UPDATE SET
                    title = excluded.title,
                    content_hash = excluded.content_hash,
                    indexed_at = excluded.indexed_at,
                    embedded = excluded.embedded
                """;
            upsert.Parameters.AddWithValue("@path", relPath);
            upsert.Parameters.AddWithValue("@title", title);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
            upsert.Parameters.AddWithValue("@embedded", embedded ? 1 : 0);
            upsert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void DeleteDocument(SqliteConnection conn, string relPath)
    {
        using var tx = conn.BeginTransaction();
        DeleteDocumentRows(conn, tx, relPath);

        using (var doc = conn.CreateCommand())
        {
            doc.Transaction = tx;
            doc.CommandText = "DELETE FROM documents WHERE path = @path";
            doc.Parameters.AddWithValue("@path", relPath);
            doc.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void DeleteDocumentRows(SqliteConnection conn, SqliteTransaction tx, string relPath)
    {
        // Order matters: chunks_fts/vec_chunks reference chunks rows, so the
        // subqueries must run before `chunks` is cleared.
        Execute(conn, tx,
            "DELETE FROM chunks_fts WHERE rowid IN (SELECT rowid FROM chunks WHERE doc_path = @path)",
            relPath);
        Execute(conn, tx,
            "DELETE FROM vec_chunks WHERE id IN (SELECT chunk_id FROM chunks WHERE doc_path = @path)",
            relPath);
        Execute(conn, tx, "DELETE FROM chunks WHERE doc_path = @path", relPath);
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, string pathParam)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@path", pathParam);
        cmd.ExecuteNonQuery();
    }
}
