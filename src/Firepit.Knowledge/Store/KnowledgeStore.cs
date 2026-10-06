using Firepit.Knowledge.Embeddings;
using Microsoft.Data.Sqlite;

namespace Firepit.Knowledge.Store;

// One store per knowledge scope — a `knowledge/` folder of Markdown truth
// plus the derived `knowledge.db` index next to it (both under the project's
// `.firepit/`). The DB is cache: delete it and the indexer rebuilds it from
// the MD files alone.
//
// Schema: `documents` doubles as the content-hash manifest (one row per MD
// file); `chunks` holds heading-scoped sections of each file; `chunks_fts`
// mirrors chunks.rowid for BM25; `vec_chunks` holds one MiniLM vector per
// chunk, keyed by the same chunk_id.
public sealed class KnowledgeStore
{
    public const string IndexFileName = "knowledge.db";

    public KnowledgeStore(string knowledgeDir, string dbPath)
        : this(knowledgeDir, dbPath, SqliteVecExtension.Default)
    {
    }

    public KnowledgeStore(string knowledgeDir, string dbPath, SqliteVecExtension vec)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeDir);
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentNullException.ThrowIfNull(vec);
        KnowledgeDir = Path.GetFullPath(knowledgeDir);
        DbPath = Path.GetFullPath(dbPath);
        Vec = vec;
    }

    public string KnowledgeDir { get; }
    public string DbPath { get; }

    /// <summary>The sqlite-vec extension this store's connections load.</summary>
    public SqliteVecExtension Vec { get; }

    // Search treats a missing DB file as "empty scope" without creating it —
    // opening a connection would plant a `.firepit/knowledge.db` in repos
    // that never opted into knowledge. Only the indexer (which requires the
    // knowledge dir to exist) and AddDocument create the file.
    public bool IndexExists => File.Exists(DbPath);

    /// <summary>
    /// A connection with everything loaded, for anything that writes.
    /// </summary>
    /// <exception cref="SqliteVecUnavailableException">sqlite-vec could not be
    /// loaded. Writing without it is not attempted: a chunk rewritten while its
    /// vector cannot be touched leaves that vector behind under an id the next
    /// write of the same chunk collides with.</exception>
    public SqliteConnection OpenConnection() => Open(requireVectors: true, out _);

    /// <summary>
    /// A connection for reads that can do without vectors — the document
    /// manifest, full-text search. When sqlite-vec cannot be loaded the
    /// connection still opens, without it, and
    /// <paramref name="vectorsUnavailable"/> says why.
    /// </summary>
    /// <remarks>
    /// <c>vec_chunks</c> cannot be queried on such a connection, but every
    /// other table can: FTS5 is compiled into SQLite, and the rest are plain
    /// tables. The schema is not touched either — creating <c>vec_chunks</c>
    /// needs the module.
    /// </remarks>
    public SqliteConnection OpenForReading(out string? vectorsUnavailable) =>
        Open(requireVectors: false, out vectorsUnavailable);

    private SqliteConnection Open(bool requireVectors, out string? vectorsUnavailable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        try
        {
            vectorsUnavailable = null;
            try
            {
                Vec.LoadInto(conn);
            }
            catch (SqliteVecUnavailableException ex) when (!requireVectors)
            {
                vectorsUnavailable = ex.Message;
            }

            using (var pragma = conn.CreateCommand())
            {
                // WAL lets search read while the indexer writes; busy_timeout
                // covers the residual writer-vs-writer window.
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            if (vectorsUnavailable is null)
            {
                EnsureSchema(conn);
            }

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    // Runs on every open — all statements are IF NOT EXISTS, which SQLite
    // resolves in microseconds against sqlite_master. Deliberately not
    // cached per instance: a user deleting knowledge.db mid-session gets a
    // correctly-schema'd rebuild on the next touch instead of SQL errors.
    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS meta(
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS documents(
                    path         TEXT PRIMARY KEY,
                    title        TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    indexed_at   TEXT NOT NULL,
                    embedded     INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS chunks(
                    chunk_id TEXT PRIMARY KEY,
                    doc_path TEXT NOT NULL,
                    heading  TEXT,
                    content  TEXT NOT NULL,
                    ord      INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_chunks_doc ON chunks(doc_path);
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(heading, content);
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                    id TEXT PRIMARY KEY,
                    embedding FLOAT[{MiniLmPipeline.EmbeddingDim}]
                );
                INSERT INTO meta(key, value) VALUES('schema_version', '1')
                    ON CONFLICT(key) DO NOTHING;
                INSERT INTO meta(key, value) VALUES('embedding_model', 'all-MiniLM-L6-v2')
                    ON CONFLICT(key) DO NOTHING;
                """;
        cmd.ExecuteNonQuery();
    }
}
