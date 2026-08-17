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

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ModelDownloader _downloader;
    private readonly EmbeddingService _embeddings;
    private readonly KnowledgeSearch _search;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _scopesGate = new();
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.OrdinalIgnoreCase);
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
                    await ReindexScopeAsync(scope, _shutdown.Token);
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
                    if (PathsEqual(existing.ProjectPath, reg.ProjectPath))
                    {
                        // `.firepit` may have appeared since the last sync —
                        // watcher attach is retried here rather than polled.
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

        List<Scope> targets;
        lock (_scopesGate)
        {
            targets = scopeNames is null || scopeNames.Count == 0
                ? [.. _scopes.Values]
                : [.. scopeNames
                    .Select(n => _scopes.GetValueOrDefault(n))
                    .OfType<Scope>()];
        }

        var all = new List<KnowledgeHit>();
        var degraded = false;
        foreach (var scope in targets)
        {
            var result = await _search.SearchAsync(scope.Store, scope.Name, query, limit, ct);
            all.AddRange(result.Hits);
            degraded |= result.Degraded;
        }

        var hits = all.OrderByDescending(h => h.Score).Take(limit).ToList();
        return new KnowledgeSearchResult(hits, degraded);
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
        await ReindexScopeAsync(scope, ct);

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
        await ReindexScopeAsync(scope, ct);

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
        await ReindexScopeAsync(scope, ct);
        return true;
    }

    private Scope CreateScope(KnowledgeScopeRegistration reg)
    {
        var projectPath = Path.GetFullPath(reg.ProjectPath);
        var location = reg.Store ?? KnowledgeStoreLocation.BesideProject(projectPath);
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
            watcher.Error += (_, e) => _logger.LogWarning(
                e.GetException(), "Knowledge watcher error for scope {Scope}", scope.Name);
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
        }
        finally
        {
            scope.IndexGate.Release();
        }
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
        public SemaphoreSlim IndexGate { get; } = new(1, 1);
        public CancellationTokenSource? PendingReindex { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            PendingReindex?.Cancel();
            IndexGate.Dispose();
        }
    }
}
