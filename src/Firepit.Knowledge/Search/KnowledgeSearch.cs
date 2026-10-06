using System.Text.RegularExpressions;
using Firepit.Knowledge.Embeddings;
using Firepit.Knowledge.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Firepit.Knowledge.Search;

// Blends semantic (vec_chunks via sqlite-vec) with lexical (chunks_fts via
// FTS5 bm25) into a single ranked list, then deduplicates chunk hits to
// document granularity. Ported from the-fishbowl (HybridSearchService).
//
// Why hybrid: embeddings catch "how do migrations work" → a doc titled
// "Lazy migration pattern" (semantic win, keyword miss); FTS catches
// specific identifiers the embedding smears over ("DatabaseFactoryV3" —
// lexical win, semantic miss). 70/30 in favour of semantic matches the
// fishbowl-tested weighting for memory-first search.
public sealed partial class KnowledgeSearch
{
    private const double VectorWeight = 0.7;
    private const double FtsWeight = 0.3;
    private const int CandidatePool = 50;
    private const int SnippetChars = 400;

    // Hard cap on the query string. A real search query is short — anything
    // past a kilobyte is a copy-paste accident or a misuse of the tool as a
    // fuzzy text-similarity API. Truncate (don't error) so the caller gets
    // *some* result back.
    public const int MaxQueryLength = 1024;

    private readonly IEmbeddingService _embeddings;
    private readonly ILogger _logger;

    public KnowledgeSearch(IEmbeddingService embeddings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Searches one scope. Returns document-level hits (best chunk per doc)
    /// plus a degraded flag when ranking fell back to FTS-only — the embedding
    /// model not ready yet, or the sqlite-vec extension not loadable.
    /// </summary>
    public async Task<KnowledgeSearchResult> SearchAsync(
        KnowledgeStore store, string scopeName, string query, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        limit = Math.Clamp(limit, 1, 100);

        query = query?.Trim() ?? string.Empty;
        if (query.Length > MaxQueryLength)
        {
            _logger.LogDebug(
                "Search query truncated from {OriginalLength} to {MaxQueryLength} chars",
                query.Length, MaxQueryLength);
            query = query[..MaxQueryLength];
        }

        if (query.Length == 0 || !store.IndexExists)
        {
            return new KnowledgeSearchResult([], Degraded: false);
        }

        // Opens without sqlite-vec when it cannot be loaded. Failing the whole
        // search over it turned a lost vector index into a lost knowledge base,
        // although FTS needs nothing the extension provides.
        using var conn = store.OpenForReading(out var vectorsUnavailable);

        // Run FTS first — it's always available; vector search is optional.
        var ftsHits = RunFts(conn, query, ct);

        List<(string Id, double Distance)> vecHits = [];
        string? degradedReason = null;
        if (vectorsUnavailable is not null)
        {
            _logger.LogDebug("sqlite-vec unavailable; falling back to FTS-only ranking: {Reason}", vectorsUnavailable);
            degradedReason =
                $"Vector search unavailable — {vectorsUnavailable}. Results are full-text only; " +
                "restarting Firepit restores semantic search.";
        }
        else
        {
            try
            {
                var vec = await _embeddings.EmbedAsync(query, ct);
                vecHits = RunVector(conn, vec, ct);
            }
            catch (EmbeddingUnavailableException ex)
            {
                _logger.LogDebug(ex, "Embedding service not ready; falling back to FTS-only ranking");
                degradedReason =
                    "Vector search unavailable (embedding model not ready) — results are full-text only.";
            }
        }

        var degraded = degradedReason is not null;
        var merged = MergeScores(vecHits, ftsHits, degraded).ToList();
        var hits = LoadTopDocuments(conn, scopeName, merged, limit, ct);
        return new KnowledgeSearchResult(hits, degraded, DegradedReason: degradedReason);
    }

    private static List<(string Id, double Bm25)> RunFts(
        SqliteConnection conn, string query, CancellationToken ct)
    {
        // FTS5's default tokenizer splits on non-alphanumeric. Matching the
        // same rule here keeps query behaviour aligned with how chunks are
        // indexed. Each token becomes a prefix match (`tok*`) and the group
        // is AND'd so every token must appear.
        var tokens = WordRegex()
            .Matches(query)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Select(t => t + "*")
            .ToList();
        if (tokens.Count == 0)
        {
            return [];
        }

        var ftsQuery = string.Join(" AND ", tokens);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.chunk_id, bm25(chunks_fts)
            FROM chunks_fts
            JOIN chunks c ON c.rowid = chunks_fts.rowid
            WHERE chunks_fts MATCH @q
            ORDER BY bm25(chunks_fts)
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@q", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", CandidatePool);

        var rows = new List<(string, double)>();
        using var reader = cmd.ExecuteReader();
        while (!ct.IsCancellationRequested && reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetDouble(1)));
        }

        return rows;
    }

    private static List<(string Id, double Distance)> RunVector(
        SqliteConnection conn, float[] queryVec, CancellationToken ct)
    {
        // sqlite-vec expects the query vector as a blob with the same float
        // layout the table stores.
        var blob = new byte[queryVec.Length * sizeof(float)];
        Buffer.BlockCopy(queryVec, 0, blob, 0, blob.Length);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, distance
            FROM vec_chunks
            WHERE embedding MATCH @q AND k = @k
            ORDER BY distance
            """;
        cmd.Parameters.AddWithValue("@q", blob);
        cmd.Parameters.AddWithValue("@k", CandidatePool);

        var rows = new List<(string, double)>();
        using var reader = cmd.ExecuteReader();
        while (!ct.IsCancellationRequested && reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetDouble(1)));
        }

        return rows;
    }

    // Normalises both score lists to [0, 1] via min-max, then linearly
    // combines them. FTS bm25 is "lower is better" — flip sign before
    // normalising. Vec distance (cosine distance on L2-normalised vectors
    // ranges [0, 2]) is also "lower is better" — flip too.
    //
    // When degraded (no vec hits), FTS carries the full signal; its
    // effective weight becomes 1.0 so the absolute numeric scores stay
    // comparable to the hybrid case at the top of the ranking.
    internal static IEnumerable<(string Id, double Score)> MergeScores(
        List<(string Id, double Distance)> vec,
        List<(string Id, double Bm25)> fts,
        bool degraded)
    {
        var vecScore = NormaliseAscending(vec);
        var ftsScore = NormaliseAscending(fts);

        var ids = new HashSet<string>(vecScore.Keys);
        ids.UnionWith(ftsScore.Keys);

        var wVec = degraded ? 0.0 : VectorWeight;
        var wFts = degraded ? 1.0 : FtsWeight;

        return ids
            .Select(id =>
            {
                var v = vecScore.TryGetValue(id, out var vs) ? vs : 0.0;
                var f = ftsScore.TryGetValue(id, out var fs) ? fs : 0.0;
                return (Id: id, Score: (wVec * v) + (wFts * f));
            })
            .OrderByDescending(x => x.Score);
    }

    // Given a list of (id, rawScore) where lower raw == better, returns
    // (id, normalised) where 1.0 is best and 0.0 is worst. Single-item or
    // all-equal lists collapse to 1.0 so any hit beats no-hit in the merge.
    internal static Dictionary<string, double> NormaliseAscending(
        IReadOnlyList<(string Id, double Raw)> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var min = rows.Min(x => x.Raw);
        var max = rows.Max(x => x.Raw);
        var span = max - min;
        if (span <= double.Epsilon)
        {
            return rows.ToDictionary(x => x.Id, _ => 1.0);
        }

        return rows.ToDictionary(x => x.Id, x => 1.0 - ((x.Raw - min) / span));
    }

    private static List<KnowledgeHit> LoadTopDocuments(
        SqliteConnection conn,
        string scopeName,
        List<(string Id, double Score)> merged,
        int limit,
        CancellationToken ct)
    {
        var hits = new List<KnowledgeHit>(limit);
        var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (chunkId, score) in merged)
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= limit)
            {
                break;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.doc_path, c.heading, c.content, d.title
                FROM chunks c
                JOIN documents d ON d.path = c.doc_path
                WHERE c.chunk_id = @id
                """;
            cmd.Parameters.AddWithValue("@id", chunkId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                // Chunk vanished between ranking and load (concurrent
                // reindex) — skip; the next search sees the fresh rows.
                continue;
            }

            var docPath = reader.GetString(0);
            if (!seenDocs.Add(docPath))
            {
                // A doc's best-ranked chunk wins; later chunks of the same
                // doc add no new result.
                continue;
            }

            var heading = reader.IsDBNull(1) ? null : reader.GetString(1);
            var content = reader.GetString(2);
            var title = reader.GetString(3);

            hits.Add(new KnowledgeHit(
                scopeName, docPath, title, heading, MakeSnippet(content), score));
        }

        return hits;
    }

    private static string MakeSnippet(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length <= SnippetChars ? trimmed : trimmed[..SnippetChars] + "…";
    }

    [GeneratedRegex(@"\w+")]
    private static partial Regex WordRegex();
}
