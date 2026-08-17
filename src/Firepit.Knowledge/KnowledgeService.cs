using System.Text;
using Firepit.Knowledge.Embeddings;
using Firepit.Knowledge.Indexing;
using Firepit.Knowledge.Search;
using Firepit.Knowledge.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Firepit.Knowledge;

/// <summary>A scope the service should index and search: one project.</summary>
/// <param name="Store">
/// Where the scope's files live. Null means the default — beside the project,
/// under its own <c>.firepit/</c>. A caller passes this to route a project's
/// docs into another project's store, which is how a public repo keeps
/// research out of its own history.
/// </param>
public sealed record KnowledgeScopeRegistration(
    string Name, string ProjectPath, KnowledgeStoreLocation? Store = null);

/// <summary>
/// Whether a scope's index can be trusted to stand in for its documents.
/// </summary>
/// <remarks>
/// The distinction exists because zero hits is an ambiguous answer. It means
/// "nothing matched" only when the base was actually searched; the rest of the
/// time it means the caller has been told nothing and cannot tell.
/// </remarks>
public enum ScopeHealth
{
    /// <summary>Registered, but no pass has finished yet — startup, or a
    /// reindex still in flight.</summary>
    NeverIndexed,

    /// <summary>Last pass read every document.</summary>
    Ready,

    /// <summary>Last pass read some documents but not all; a retry is due.</summary>
    Partial,

    /// <summary>Last pass threw. The index is whatever it was before.</summary>
    Failed,
}

// The one object the app wires up. Owns the embedding pipeline (one ONNX
// session per app), one store/indexer/watcher per registered scope, and the
// multi-scope search. Scope names are caller-defined; by convention the
// `.firepit` meta project registers as "global" and every other project by
// its project name — "one mechanism, two scopes" (ROADMAP M9).
//
// Layout per scope: `{project}/.firepit/knowledge/*.md` is committed truth,
// `{project}/.firepit/knowledge.db` the derived index. The service never
// creates either unless the project opted in (knowledge dir exists) or the
// user explicitly adds a document.
public sealed class KnowledgeService : IDisposable
{
    public const string GlobalScopeName = "global";
    private const int DebounceMs = 750;

    /// <summary>
    /// How often the safety sweep runs. It is not how the index normally keeps
    /// up — the watcher does that, in under a second — it is the backstop for
    /// every way a watcher can stop delivering without saying so: a share that
    /// went away, a buffer overflow, a directory that did not exist when the
    /// scope was created. Cheap enough to leave running: one directory
    /// enumeration per scope, and a reindex only when the contents actually
    /// differ from what was last indexed.
    /// </summary>
    private const int SafetySweepMs = 120_000;

    /// <summary>
    /// How long to wait before repeating a pass that could not read every
    /// document. Tuned to an editor's save, which is the usual cause.
    /// </summary>
    private const int RetryMs = 3_000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ModelDownloader _downloader;
    private readonly EmbeddingService _embeddings;
    private readonly KnowledgeSearch _search;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _scopesGate = new();
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _sweep;
    private bool _disposed;

    public KnowledgeService(string modelDataRoot, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelDataRoot);
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<KnowledgeService>();
        _downloader = new ModelDownloader(
            modelDataRoot, logger: _loggerFactory.CreateLogger<ModelDownloader>());
        _embeddings = new EmbeddingService(
            _downloader, _loggerFactory.CreateLogger<EmbeddingService>());
        _search = new KnowledgeSearch(
            _embeddings, _loggerFactory.CreateLogger<KnowledgeSearch>());
        _sweep = new Timer(
            _ => SafetySweep(), null, SafetySweepMs, SafetySweepMs);
    }

    /// <summary>
    /// Catches what the watchers missed. Re-attaches watchers that are gone and
    /// reindexes any scope whose directory no longer matches what was last
    /// indexed. Exposed for tests; the timer is the normal caller.
    /// </summary>
    internal void SafetySweep()
    {
        if (_disposed)
        {
            return;
        }

        // The sweep runs on a timer callback, which means an escaping exception
        // does not fail a request — it takes the process down. Everything below
        // is best-effort maintenance; none of it is worth that.
        try
        {
            List<Scope> scopes;
            lock (_scopesGate)
            {
                scopes = _scopes.Values.ToList();
                foreach (var scope in scopes.Where(s => s.Watcher is null))
                {
                    TryAttachWatcher(scope);
                }
            }

            foreach (var scope in scopes)
            {
                try
                {
                    // Anything but Ready means the last pass did not fully land,
                    // so the fingerprint says nothing and the pass is repeated.
                    if (scope.Health != ScopeHealth.Ready ||
                        Fingerprint(scope.Store.KnowledgeDir) != scope.IndexedFingerprint)
                    {
                        _logger.LogInformation(
                            "Knowledge scope {Scope}: sweep found changes the watcher missed", scope.Name);
                        ScheduleReindex(scope);
                    }
                }
                catch (Exception ex)
                {
                    // An unreachable share is not a reason to stop sweeping the rest.
                    _logger.LogDebug(ex, "Knowledge sweep skipped scope {Scope}", scope.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Knowledge safety sweep failed");
        }
    }

    /// <summary>
    /// Cheap stand-in for the directory's content: how many documents, and the
    /// newest write among them. Enough to notice an edit, an addition or a
    /// deletion without reading a single file.
    /// </summary>
    private static (int Count, long Newest) Fingerprint(string docsDir)
    {
        if (!Directory.Exists(docsDir))
        {
            return (0, 0);
        }

        var count = 0;
        long newest = 0;
        foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
        {
            count++;
            var ticks = File.GetLastWriteTimeUtc(file).Ticks;
            if (ticks > newest)
            {
                newest = ticks;
            }
        }

        return (count, newest);
    }

    public static string GetKnowledgeDir(string projectPath) =>
        Path.Combine(projectPath, ".firepit", "knowledge");

    public static string GetIndexPath(string projectPath) =>
        Path.Combine(projectPath, ".firepit", KnowledgeStore.IndexFileName);

    public IReadOnlyList<string> ScopeNames
    {
        get
        {
            lock (_scopesGate)
            {
                return [.. _scopes.Keys];
            }
        }
    }

    /// <summary>
    /// Kicks off the first-run model download in the background. Detached on
    /// purpose — blocking startup on a ~90MB download would make the app
    /// look hung. Once the model is usable, every scope gets a backfill pass
    /// so docs indexed FTS-only pick up their vectors.
    /// </summary>
    public void StartModelDownload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_downloader.IsReady())
                {
                    await _downloader.EnsureModelAsync(_shutdown.Token);
                    _logger.LogInformation("Embedding model ready");
                }

                List<Scope> scopes;
                lock (_scopesGate)
                {
                    scopes = [.. _scopes.Values];
                }

                foreach (var scope in scopes)
                {
                    try
                    {
                        await ReindexScopeAsync(scope, _shutdown.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One scope failing its backfill must not cost the
                        // others their vectors. The scope is marked Failed and
                        // searches against it say so.
                        _logger.LogWarning(
                            ex, "Embedding backfill failed for scope {Scope}", scope.Name);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Embedding model download failed — knowledge search runs FTS-only until next start");
            }
        });
    }

    /// <summary>
    /// Reconciles the registered scopes with the given set: new scopes are
    /// added (and indexed in the background), scopes no longer listed are
    /// dropped. Idempotent — call it on every project-list reload.
    /// </summary>
    public void SyncScopes(IReadOnlyCollection<KnowledgeScopeRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(registrations);

        var added = new List<Scope>();
        lock (_scopesGate)
        {
            var wanted = new Dictionary<string, KnowledgeScopeRegistration>(StringComparer.OrdinalIgnoreCase);
            foreach (var reg in registrations)
            {
                wanted[reg.Name] = reg;
            }

            foreach (var gone in _scopes.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                _scopes[gone].Dispose();
                _scopes.Remove(gone);
            }

            foreach (var (name, reg) in wanted)
            {
                if (_scopes.TryGetValue(name, out var existing))
                {
                    // The docs directory is part of the identity, not just the
                    // project path: a pointer file can move a scope's storage
                    // while the project stays exactly where it is, and treating
                    // that as unchanged would keep indexing the old location
                    // until the next restart.
                    var wantedDocs = (reg.Store ?? KnowledgeStoreLocation.For(reg.ProjectPath)).DocsDir;
                    if (PathsEqual(existing.ProjectPath, reg.ProjectPath) &&
                        PathsEqual(existing.Store.KnowledgeDir, wantedDocs))
                    {
                        // The parent directory may have appeared since the last
                        // sync — watcher attach is retried here rather than
                        // polled.
                        if (existing.Watcher is null)
                        {
                            TryAttachWatcher(existing);
                        }

                        continue;
                    }

                    existing.Dispose();
                    _scopes.Remove(name);
                }

                var scope = CreateScope(reg);
                _scopes[name] = scope;
                added.Add(scope);
            }
        }

        foreach (var scope in added)
        {
            ScheduleReindex(scope);
        }
    }

    /// <summary>
    /// Hybrid search across the given scopes (all registered scopes when
    /// <paramref name="scopeNames"/> is null/empty). Per-scope scores are
    /// min-max normalised, so cross-scope merging compares like with like.
    /// </summary>
    public async Task<KnowledgeSearchResult> SearchAsync(
        string query,
        IReadOnlyCollection<string>? scopeNames = null,
        int limit = 8,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var warnings = new List<string>();
        List<Scope> targets;
        lock (_scopesGate)
        {
            if (scopeNames is null || scopeNames.Count == 0)
            {
                targets = [.. _scopes.Values];
            }
            else
            {
                targets = [];
                foreach (var name in scopeNames)
                {
                    if (_scopes.TryGetValue(name, out var scope))
                    {
                        targets.Add(scope);
                        continue;
                    }

                    // Dropping the name silently is how a typo, a renamed
                    // project or a scope disabled by a broken pointer turns
                    // into "there is nothing on that subject".
                    warnings.Add(
                        $"No knowledge base named '{name}' — it was not searched. " +
                        $"Known: {string.Join(", ", _scopes.Keys.Order())}");
                }
            }

            foreach (var scope in targets)
            {
                switch (scope.Health)
                {
                    case ScopeHealth.NeverIndexed:
                        warnings.Add(
                            $"'{scope.Name}' has not finished indexing yet — results from it may be incomplete.");
                        break;
                    case ScopeHealth.Partial:
                        warnings.Add(
                            $"'{scope.Name}' is partly indexed: some documents could not be read on the last pass.");
                        break;
                    case ScopeHealth.Failed:
                        warnings.Add(
                            $"'{scope.Name}' failed to index ({scope.FailureReason}) — its results are stale or missing.");
                        break;
                }
            }
        }

        var all = new List<KnowledgeHit>();
        var degraded = false;
        foreach (var scope in targets)
        {
            try
            {
                var result = await _search.SearchAsync(scope.Store, scope.Name, query, limit, ct);
                all.AddRange(result.Hits);
                degraded |= result.Degraded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable base must not take the healthy ones with it,
                // and must not pass for an empty one either.
                _logger.LogWarning(ex, "Knowledge search failed for scope {Scope}", scope.Name);
                warnings.Add($"'{scope.Name}' could not be searched: {ex.Message}");
            }
        }

        var hits = all.OrderByDescending(h => h.Score).Take(limit).ToList();
        return new KnowledgeSearchResult(hits, degraded, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>
    /// Compares each scope's index against the documents on disk, and
    /// optionally repairs what is derived.
    /// </summary>
    /// <remarks>
    /// The watchers, the retry and the sweep all aim to keep this from ever
    /// finding anything. They are event-driven, though, and every event source
    /// can fail quietly — so this is the pass that answers "is it actually
    /// correct" by looking, rather than by trusting the machinery that was
    /// supposed to keep it correct.
    ///
    /// Repair only ever touches the index, which is derived from the markdown
    /// and can always be rebuilt from it. Nothing here edits a document.
    /// </remarks>
    public async Task<IReadOnlyList<ScopeIntegrity>> CheckIntegrityAsync(
        IReadOnlyCollection<string>? scopeNames = null,
        bool repair = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<Scope> targets;
        List<string> unregistered = [];
        lock (_scopesGate)
        {
            if (scopeNames is null || scopeNames.Count == 0)
            {
                targets = [.. _scopes.Values];
            }
            else
            {
                targets = [];
                foreach (var name in scopeNames)
                {
                    if (_scopes.TryGetValue(name, out var scope))
                    {
                        targets.Add(scope);
                    }
                    else
                    {
                        // Reported, not skipped. Dropping it returned an empty
                        // list, which every caller reads as "nothing wrong".
                        unregistered.Add(name);
                    }
                }
            }
        }

        var results = new List<ScopeIntegrity>();
        foreach (var name in unregistered)
        {
            results.Add(ScopeIntegrity.Unregistered(name));
        }

        foreach (var scope in targets)
        {
            results.Add(await CheckScopeAsync(scope, repair, ct));
        }

        return results;
    }

    private async Task<ScopeIntegrity> CheckScopeAsync(Scope scope, bool repair, CancellationToken ct)
    {
        var repairs = new List<string>();
        var docsDir = scope.Store.KnowledgeDir;

        var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(docsDir))
        {
            foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(docsDir, file).Replace('\\', '/');
                try
                {
                    onDisk[rel] = await Store.NonBlockingFile.HashAsync(file, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable right now. Recorded with a hash nothing can
                    // match, so it shows up as needing attention rather than
                    // passing as indexed.
                    onDisk[rel] = "<unreadable>";
                }
            }
        }

        Dictionary<string, (string Hash, bool Embedded)> indexed;
        try
        {
            indexed = ReadIndexManifest(scope);
        }
        catch (Exception ex)
        {
            // A corrupt index is total loss for the scope, and it is derived
            // data — so the repair is to throw it away and rebuild from the
            // markdown, which is the truth.
            if (repair)
            {
                repairs.Add(await RebuildIndexAsync(scope, ct));
                try
                {
                    indexed = ReadIndexManifest(scope);
                }
                catch (Exception second)
                {
                    return new ScopeIntegrity(
                        scope.Name, docsDir, scope.Health, onDisk.Count, 0, [.. onDisk.Keys], [], [],
                        second.Message, repairs);
                }
            }
            else
            {
                return new ScopeIntegrity(
                    scope.Name, docsDir, scope.Health, onDisk.Count, 0, [.. onDisk.Keys], [], [],
                    ex.Message, repairs);
            }
        }

        var missing = onDisk.Keys.Where(k => !indexed.ContainsKey(k)).Order().ToList();
        var stale = indexed.Keys.Where(k => !onDisk.ContainsKey(k)).Order().ToList();
        var outOfDate = onDisk
            .Where(kv => indexed.TryGetValue(kv.Key, out var row) && row.Hash != kv.Value)
            .Select(kv => kv.Key)
            .Order()
            .ToList();

        if (repair && (missing.Count > 0 || stale.Count > 0 || outOfDate.Count > 0 ||
                       scope.Health is not ScopeHealth.Ready))
        {
            await ReindexAfterWriteAsync(scope, ct);
            repairs.Add("reindexed");
            indexed = TryReadIndexManifest(scope);
            missing = onDisk.Keys.Where(k => !indexed.ContainsKey(k)).Order().ToList();
            stale = indexed.Keys.Where(k => !onDisk.ContainsKey(k)).Order().ToList();
            outOfDate = onDisk
                .Where(kv => indexed.TryGetValue(kv.Key, out var row) && row.Hash != kv.Value)
                .Select(kv => kv.Key)
                .Order()
                .ToList();
        }

        // A watcher that is not attached means this scope is running blind
        // until the next sweep. Cheap to fix here while we are looking anyway.
        lock (_scopesGate)
        {
            if (scope.Watcher is null)
            {
                TryAttachWatcher(scope);
                if (scope.Watcher is not null)
                {
                    repairs.Add("re-attached the file watcher");
                }
            }
        }

        if (repair && PinnedDigest.Regenerate(docsDir, scope.DigestPath))
        {
            repairs.Add("regenerated knowledge-pinned.md");
        }

        return new ScopeIntegrity(
            scope.Name, docsDir, scope.Health, onDisk.Count, indexed.Count,
            missing, stale, outOfDate, null, repairs);
    }

    private static Dictionary<string, (string Hash, bool Embedded)> ReadIndexManifest(Scope scope)
    {
        var result = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        if (!scope.Store.IndexExists)
        {
            return result;
        }

        using var conn = scope.Store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, content_hash, embedded FROM documents";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2) != 0);
        }

        return result;
    }

    private static Dictionary<string, (string Hash, bool Embedded)> TryReadIndexManifest(Scope scope)
    {
        try
        {
            return ReadIndexManifest(scope);
        }
        catch (Exception)
        {
            return new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<string> RebuildIndexAsync(Scope scope, CancellationToken ct)
    {
        await scope.IndexGate.WaitAsync(ct);
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = scope.Store.DbPath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            scope.IndexGate.Release();
        }

        scope.Health = ScopeHealth.NeverIndexed;
        scope.IndexedFingerprint = default;
        await ReindexAfterWriteAsync(scope, ct);
        return "deleted the unreadable index and rebuilt it from the documents";
    }

    /// <summary>Reads one knowledge document. Null when it doesn't exist.</summary>
    public async Task<KnowledgeDocument?> GetDocumentAsync(
        string scopeName, string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scope = RequireScope(scopeName);

        var full = ResolveInsideKnowledgeDir(scope, path);
        if (full is null || !File.Exists(full))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(full, ct);
        var (title, _) = MarkdownChunker.Chunk(Path.GetFileName(full), text);
        var rel = Path.GetRelativePath(scope.Store.KnowledgeDir, full).Replace('\\', '/');
        return new KnowledgeDocument(scope.Name, rel, title, text);
    }

    /// <summary>
    /// Writes a new knowledge document (slugged file name derived from the
    /// title) and indexes it before returning, so an immediately-following
    /// search finds it. <paramref name="pinned"/> marks the doc `pin: true` —
    /// the always-on tier compiled into <c>.firepit/knowledge-pinned.md</c>.
    /// </summary>
    public async Task<KnowledgeDocument> AddDocumentAsync(
        string scopeName, string title, string content, bool pinned = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(content);
        var scope = RequireScope(scopeName);

        // Explicit user intent — creating `.firepit/knowledge/` here is the
        // opt-in, unlike the indexer which never plants folders.
        Directory.CreateDirectory(scope.Store.KnowledgeDir);

        var slug = Slugify(title);
        var fileName = slug + ".md";
        var full = Path.Combine(scope.Store.KnowledgeDir, fileName);
        var suffix = 2;
        while (File.Exists(full))
        {
            fileName = $"{slug}-{suffix++}.md";
            full = Path.Combine(scope.Store.KnowledgeDir, fileName);
        }

        var body = content.TrimStart().StartsWith("# ", StringComparison.Ordinal)
            ? content
            : $"# {title}\n\n{content}";
        if (pinned)
        {
            body = PinnedFrontmatter.SetPinned(body, true);
        }

        if (!body.EndsWith('\n'))
        {
            body += "\n";
        }

        await File.WriteAllTextAsync(full, body, ct);

        lock (_scopesGate)
        {
            if (scope.Watcher is null)
            {
                TryAttachWatcher(scope);
            }
        }

        // Index right away; the watcher's debounced pass afterwards no-ops
        // via the content-hash manifest.
        await ReindexAfterWriteAsync(scope, ct);

        return new KnowledgeDocument(scope.Name, fileName, title, body);
    }

    /// <summary>
    /// Replaces an existing document's content in place and re-indexes it
    /// (old chunks, FTS rows and vectors are dropped) before returning.
    /// Null when the document doesn't exist. <paramref name="pinned"/>:
    /// true/false toggles the `pin: true` frontmatter, null keeps the
    /// document's current pin state.
    /// </summary>
    public async Task<KnowledgeDocument?> UpdateDocumentAsync(
        string scopeName, string path, string content, string? title = null,
        bool? pinned = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        var scope = RequireScope(scopeName);

        var full = ResolveInsideKnowledgeDir(scope, path);
        if (full is null || !File.Exists(full))
        {
            return null;
        }

        var existing = await File.ReadAllTextAsync(full, ct);

        var trimmed = content.TrimStart();
        var body = title is not null &&
                   !trimmed.StartsWith("# ", StringComparison.Ordinal) &&
                   !trimmed.StartsWith("---", StringComparison.Ordinal)
            ? $"# {title}\n\n{content}"
            : content;

        // Replace semantics for the CONTENT, preserve semantics for the pin:
        // an update that doesn't mention pinning must not silently unpin.
        var wantPinned = pinned ?? PinnedFrontmatter.IsPinned(existing);
        if (wantPinned != PinnedFrontmatter.IsPinned(body))
        {
            body = PinnedFrontmatter.SetPinned(body, wantPinned);
        }

        if (!body.EndsWith('\n'))
        {
            body += "\n";
        }

        await File.WriteAllTextAsync(full, body, ct);
        await ReindexAfterWriteAsync(scope, ct);

        var (resolvedTitle, _) = MarkdownChunker.Chunk(Path.GetFileName(full), body);
        var rel = Path.GetRelativePath(scope.Store.KnowledgeDir, full).Replace('\\', '/');
        return new KnowledgeDocument(scope.Name, rel, resolvedTitle, body);
    }

    /// <summary>
    /// Deletes a document file and removes it from the index (chunks, FTS,
    /// vectors — and the pinned digest, if it was pinned) before returning.
    /// False when the document doesn't exist.
    /// </summary>
    public async Task<bool> DeleteDocumentAsync(
        string scopeName, string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scope = RequireScope(scopeName);

        var full = ResolveInsideKnowledgeDir(scope, path);
        if (full is null || !File.Exists(full))
        {
            return false;
        }

        File.Delete(full);
        await ReindexAfterWriteAsync(scope, ct);
        return true;
    }

    private Scope CreateScope(KnowledgeScopeRegistration reg)
    {
        var projectPath = Path.GetFullPath(reg.ProjectPath);
        var location = reg.Store ?? KnowledgeStoreLocation.For(projectPath);
        var store = new KnowledgeStore(location.DocsDir, location.IndexPath);
        var scope = new Scope
        {
            Name = reg.Name,
            ProjectPath = projectPath,
            DigestPath = location.DigestPath,
            Store = store,
            Indexer = new KnowledgeIndexer(
                store, _embeddings, _loggerFactory.CreateLogger<KnowledgeIndexer>()),
        };
        TryAttachWatcher(scope);
        return scope;
    }

    // Watches the *parent* of the knowledge dir for *.md changes and funnels
    // knowledge-dir hits into a debounced rescan. Watching the parent (not the
    // knowledge dir itself) means the watcher survives the knowledge dir being
    // created/deleted while the app runs; inbox/runs traffic is filtered out by
    // path below. For the default layout the parent is the project's
    // `.firepit/`; for a redirected store it is the store's `knowledge/`, where
    // the same filter keeps sibling scopes from waking each other.
    private void TryAttachWatcher(Scope scope)
    {
        var firepitDir = Path.GetDirectoryName(scope.Store.KnowledgeDir);
        if (firepitDir is null || !Directory.Exists(firepitDir))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(firepitDir, "*.md")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Changed += (_, e) => OnKnowledgeFileEvent(scope, e.FullPath);
            watcher.Created += (_, e) => OnKnowledgeFileEvent(scope, e.FullPath);
            watcher.Deleted += (_, e) => OnKnowledgeFileEvent(scope, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnKnowledgeFileEvent(scope, e.OldFullPath);
                OnKnowledgeFileEvent(scope, e.FullPath);
            };
            // A watcher that errors is usually done for — buffer overflow, or
            // a network share going away. Dropping the reference is what makes
            // it recoverable: the sweep re-attaches a null watcher, whereas a
            // dead object that still looks alive means the scope silently
            // stops updating until the app restarts.
            watcher.Error += (_, e) =>
            {
                _logger.LogWarning(
                    e.GetException(), "Knowledge watcher lost for scope {Scope} — will re-attach", scope.Name);
                lock (_scopesGate)
                {
                    if (ReferenceEquals(scope.Watcher, watcher))
                    {
                        scope.Watcher = null;
                    }
                }

                try { watcher.Dispose(); } catch { /* already gone */ }
                ScheduleReindex(scope);
            };
            watcher.EnableRaisingEvents = true;
            scope.Watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Could not watch {Dir} for knowledge changes", firepitDir);
        }
    }

    private void OnKnowledgeFileEvent(Scope scope, string fullPath)
    {
        if (!IsUnder(scope.Store.KnowledgeDir, fullPath))
        {
            return;
        }

        ScheduleReindex(scope);
    }

    private void ScheduleReindex(Scope scope)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_scopesGate)
        {
            scope.PendingReindex?.Cancel();
            scope.PendingReindex = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cts = scope.PendingReindex;
        }

        var token = cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(DebounceMs, token);
                    await ReindexScopeAsync(scope, token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Knowledge reindex failed for scope {Scope}", scope.Name);
                }
            },
            token);
    }

    private async Task ReindexScopeAsync(Scope scope, CancellationToken ct)
    {
        // One indexer pass per scope at a time; searches don't take this
        // gate — WAL keeps them consistent alongside a writer.
        await scope.IndexGate.WaitAsync(ct);
        try
        {
            var stats = await scope.Indexer.ReindexAsync(ct);
            if (stats.ChangedAnything || stats.PendingEmbedding > 0)
            {
                _logger.LogInformation(
                    "Knowledge scope {Scope}: {Indexed} indexed, {Removed} removed, {Pending} awaiting embeddings",
                    scope.Name, stats.Indexed, stats.Removed, stats.PendingEmbedding);
            }

            // The always-on tier rides the same pass: any change to the
            // markdown (tool call, hand edit, delete) refreshes the digest.
            // It never sits inside the knowledge dir, so it cannot index
            // itself, and the watcher's knowledge-dir filter ignores the write.
            // With a redirected store the digest stays in the project while
            // the docs are elsewhere — hence a path of its own.
            if (PinnedDigest.Regenerate(scope.Store.KnowledgeDir, scope.DigestPath))
            {
                _logger.LogInformation(
                    "Knowledge scope {Scope}: pinned digest updated", scope.Name);
            }

            // A documents directory that is simply not there yet is the normal
            // state of most projects, and answering "nothing known" for one is
            // correct. A directory that held documents and is now gone is not
            // the same thing: the pass deletes every row and then reports a
            // healthy, empty base — a search says nothing, and nothing in the
            // answer distinguishes that from a base that was always empty.
            if (stats.Removed > 0 && !Directory.Exists(scope.Store.KnowledgeDir))
            {
                scope.Health = ScopeHealth.Failed;
                scope.FailureReason =
                    $"the documents directory '{scope.Store.KnowledgeDir}' no longer exists, " +
                    $"and {stats.Removed} document(s) were dropped from the index";
                _logger.LogError(
                    "Knowledge scope {Scope}: {Dir} disappeared; {Removed} document(s) dropped",
                    scope.Name, scope.Store.KnowledgeDir, stats.Removed);
                return;
            }

            if (stats.Complete)
            {
                scope.Health = ScopeHealth.Ready;
                scope.FailureReason = null;

                // Recorded after the pass, not before: a write that lands
                // mid-pass then differs from the fingerprint and the sweep
                // picks it up, rather than being recorded as already indexed.
                scope.IndexedFingerprint = Fingerprint(scope.Store.KnowledgeDir);
            }
            else
            {
                // Deliberately no fingerprint. Recording one here would say
                // "this directory is fully indexed" about a pass that skipped
                // documents, and the sweep — which only acts on drift — would
                // never come back for them.
                scope.Health = ScopeHealth.Partial;
                _logger.LogInformation(
                    "Knowledge scope {Scope}: {Skipped} document(s) unreadable this pass — will retry",
                    scope.Name, stats.Skipped);
                ScheduleRetry(scope);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The index is whatever the last good pass left. Saying so beats
            // answering searches from it as though it were current.
            scope.Health = ScopeHealth.Failed;
            scope.FailureReason = ex.Message;
            throw;
        }
        finally
        {
            scope.IndexGate.Release();
        }
    }

    /// <summary>
    /// Indexes after a write that already landed on disk.
    /// </summary>
    /// <remarks>
    /// Failure here must not read as "the document was not saved". The file is
    /// the source of truth and it is written; reporting a failure would invite
    /// the caller to write it again, and a second copy under a new slug is a
    /// worse outcome than an index that catches up a moment later. The scope is
    /// marked <see cref="ScopeHealth.Failed"/>, so searches say so, and the
    /// sweep retries.
    /// </remarks>
    private async Task ReindexAfterWriteAsync(Scope scope, CancellationToken ct)
    {
        try
        {
            await ReindexScopeAsync(scope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Knowledge scope {Scope}: indexing failed after a write — the file is on disk",
                scope.Name);
        }
    }

    /// <summary>
    /// Comes back for an incomplete pass sooner than the sweep would. Files are
    /// usually locked for the length of an editor's save, not for minutes.
    /// </summary>
    private void ScheduleRetry(Scope scope)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RetryMs, _shutdown.Token);
                await ReindexScopeAsync(scope, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The sweep is still behind this; it retries on drift, and an
                // incomplete pass never recorded a fingerprint to drift from.
                _logger.LogDebug(ex, "Knowledge retry failed for scope {Scope}", scope.Name);
            }
        });
    }

    private Scope RequireScope(string scopeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        lock (_scopesGate)
        {
            if (_scopes.TryGetValue(scopeName, out var scope))
            {
                return scope;
            }
        }

        throw new ArgumentException($"Unknown knowledge scope: {scopeName}", nameof(scopeName));
    }

    // MCP input is untrusted — a `..`-laden path must not escape the
    // knowledge dir.
    private static string? ResolveInsideKnowledgeDir(Scope scope, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = scope.Store.KnowledgeDir;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static bool IsUnder(string dir, string fullPath) =>
        fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Slugify(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "note" : slug;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweep.Dispose();
        _shutdown.Cancel();
        lock (_scopesGate)
        {
            foreach (var scope in _scopes.Values)
            {
                scope.Dispose();
            }

            _scopes.Clear();
        }

        _embeddings.Dispose();
        _shutdown.Dispose();
    }

    private sealed class Scope : IDisposable
    {
        public required string Name { get; init; }
        public required string ProjectPath { get; init; }

        /// <summary>Where this scope's generated knowledge-pinned.md goes. Not
        /// derivable from the docs dir — the docs may live in another project's
        /// store, but the digest belongs to this project's CLAUDE.md.</summary>
        public required string DigestPath { get; init; }

        public required KnowledgeStore Store { get; init; }
        public required KnowledgeIndexer Indexer { get; init; }
        public FileSystemWatcher? Watcher { get; set; }

        /// <summary>What the directory looked like at the end of the last index
        /// pass. The sweep compares against it to spot changes no watcher
        /// reported. Only recorded after a pass that read every document —
        /// otherwise the skipped ones would look indexed and never be
        /// retried.</summary>
        public (int Count, long Newest) IndexedFingerprint { get; set; }

        /// <summary>Whether this scope's index can be trusted to answer for it.</summary>
        public ScopeHealth Health { get; set; } = ScopeHealth.NeverIndexed;

        /// <summary>Why <see cref="Health"/> is <see cref="ScopeHealth.Failed"/>.</summary>
        public string? FailureReason { get; set; }
        public SemaphoreSlim IndexGate { get; } = new(1, 1);
        public CancellationTokenSource? PendingReindex { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            PendingReindex?.Cancel();

            // IndexGate is deliberately not disposed. A scope can be replaced
            // (a changed documents directory) or the service shut down while an
            // index pass still holds it, and that pass releases the gate from a
            // finally — which would then throw ObjectDisposedException out of a
            // finally block, and out of RebuildIndexAsync into an integrity
            // check. SemaphoreSlim only needs disposal once its
            // AvailableWaitHandle has been touched, which nothing here does.
        }
    }
}
