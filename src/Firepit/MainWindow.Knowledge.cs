using System.IO;
using System.Threading.Tasks;
using Firepit.Core.ProjectConfig;
using Firepit.Knowledge;
using Firepit.Mcp;
using Serilog;
using SerilogLoggerFactory = Serilog.Extensions.Logging.SerilogLoggerFactory;

namespace Firepit;

/// <summary>
/// Knowledge subsystem wiring (ROADMAP M9): one KnowledgeService for the app,
/// scopes synced from the project list, plus the IMcpBackend knowledge
/// members. Unlike the other backend members these never marshal onto the
/// dispatcher — KnowledgeService is thread-safe and touches no UI state.
/// </summary>
public partial class MainWindow
{
    private KnowledgeService? _knowledgeService;
    private SerilogLoggerFactory? _knowledgeLoggerFactory;

    // Discovery names the meta project after its folder (".firepit"), but its
    // knowledge registers as the "global" scope. Remembered here so tool
    // calls originating *inside* the meta project resolve to "global" too.
    private string? _metaProjectName;

    /// <summary>The central repo. Shared by knowledge, blueprints and scaffolding.</summary>
    internal string MetaProjectPath =>
        Path.GetFullPath(Path.Combine(_settings.ProjectsRoot, ".firepit"));

    // Scopes that asked for a store we could not resolve, with the reason.
    // Kept so the knowledge tools can answer "why is my scope gone" instead of
    // the caller guessing from a bare not-found.
    private IReadOnlyDictionary<string, string> _brokenKnowledgeScopes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Project scope name → the directory its docs actually live in. Only
    // redirected scopes appear here, and only so the tools stop telling an
    // agent to commit a file that deliberately is not in this repo.
    private IReadOnlyDictionary<string, string> _redirectedKnowledgeScopes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Project scope name → the scope actually registered for it. Differs when
    // a pointer sends several projects at one shared base.
    private IReadOnlyDictionary<string, string> _knowledgeScopeAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void InitializeKnowledgeService()
    {
        try
        {
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Firepit");
            _knowledgeLoggerFactory = new SerilogLoggerFactory(Log.Logger);
            _knowledgeService = new KnowledgeService(dataRoot, _knowledgeLoggerFactory);
            SyncKnowledgeScopes();
            _knowledgeService.StartModelDownload();
            Log.Information(
                "Knowledge service started: {Count} scope(s)",
                _knowledgeService.ScopeNames.Count);
        }
        catch (Exception ex)
        {
            // Knowledge is an assist feature — a failure here must never
            // block the shell from starting.
            Log.Error(ex, "Failed to start knowledge service");
            _knowledgeService = null;
        }
    }

    /// <summary>Reconcile knowledge scopes with the current project list.
    /// Safe to call any time; no-op before InitializeKnowledgeService.</summary>
    private void SyncKnowledgeScopes()
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return;
        }

        try
        {
            var metaPath = Path.GetFullPath(Path.Combine(_settings.ProjectsRoot, ".firepit"));

            var registrations = new List<KnowledgeScopeRegistration>();
            var broken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var redirected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byDocsDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The base every project reads. It lives at the root of the meta
            // repo, not in its .firepit/ — what the administration holds about
            // all projects is not one project's own data.
            var legacyGlobal = KnowledgeLayout.UsesLegacyGlobal(metaPath);
            registrations.Add(new KnowledgeScopeRegistration(
                KnowledgeService.GlobalScopeName,
                metaPath,
                KnowledgeStoreLocation.ForGlobal(metaPath)));
            taken.Add(KnowledgeService.GlobalScopeName);
            byDocsDir[KnowledgeLayout.ResolveGlobalDocsDir(metaPath)] =
                KnowledgeService.GlobalScopeName;

            // Project names are the primary namespace: a shared base may not
            // be named after a directory that some project already answers to.
            // Without this a manual entry pointing at D:\private\notes\knowledge
            // could claim the name "notes" before the project folder `notes`
            // was reached, and the later registration would silently take the
            // name back — routing the first project's writes into the second
            // project's repository.
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                KnowledgeService.GlobalScopeName,
            };
            foreach (var project in _allProjects)
            {
                reserved.Add(project.Name);
            }

            if (legacyGlobal)
            {
                Log.Information(
                    "Global knowledge is still at the pre-split path {Dir}. Move it to {Target} " +
                    "to give the meta project its own local knowledge.",
                    KnowledgeLayout.LocalDocsDir(metaPath), KnowledgeLayout.GlobalDocsDir(metaPath));
            }

            // Projects keeping their own knowledge are registered before those
            // pointing at someone else's. A base's owner then always claims it
            // first, and the borrowers alias to it — so three repos sharing
            // just-knowledge's base read as "just-knowledge" rather than as
            // whichever of them the project scan happened to reach first.
            // Resolution is cheap (one stat, or one small file read) and its
            // result is reused below.
            var ordered = _allProjects
                .Select(p => (Project: p, Resolution: KnowledgeLocator.Resolve(p.Path)))
                .OrderBy(x => x.Resolution.IsRedirected ? 1 : 0)
                .ToList();

            foreach (var (project, preresolved) in ordered)
            {
                var isMeta = string.Equals(
                    Path.GetFullPath(project.Path), metaPath, StringComparison.OrdinalIgnoreCase);
                if (isMeta)
                {
                    _metaProjectName = project.Name;

                    // While the old layout is in place its .firepit/knowledge
                    // IS the global base, so registering it again as a local
                    // scope would index the same files twice under two names.
                    if (legacyGlobal)
                    {
                        aliases[project.Name] = KnowledgeService.GlobalScopeName;
                        if (ConfiguredId(project) is { } metaId)
                        {
                            aliases[metaId] = KnowledgeService.GlobalScopeName;
                        }

                        continue;
                    }
                }

                var projectScope = project.Name;

                // Tracked separately from `aliases`, which now also holds
                // configured-id entries: an id that happens to match another
                // project's folder name would otherwise make that project look
                // already-handled and drop it from registration entirely.
                if (!handled.Add(projectScope))
                {
                    continue;
                }

                var configuredId = ConfiguredId(project);

                var resolution = preresolved;
                if (resolution.Error is { } error)
                {
                    // Deliberately not registered. A scope that quietly fell
                    // back to the project's own folder would put research into
                    // the repo the pointer exists to keep it out of — better
                    // the tools report the scope as broken, and say why.
                    // Recorded under the configured id too: that is the name the
                    // session exports, so keying by folder name alone turned a
                    // specific reason into a bare "unknown scope".
                    broken[projectScope] = error;
                    if (configuredId is not null)
                    {
                        broken[configuredId] = error;
                    }

                    Log.Error("Knowledge scope {Scope} disabled: {Reason}", projectScope, error);
                    continue;
                }

                if (resolution.IsRedirected)
                {
                    redirected[projectScope] = resolution.DocsDir;
                    if (configuredId is not null)
                    {
                        redirected[configuredId] = resolution.DocsDir;
                    }

                    ExcludePinnedDigest(project, resolution.DocsDir);
                }

                // Several projects pointing at one directory are one knowledge
                // base, not N. Registering each separately would have that many
                // watchers and indexers fighting over the same files.
                if (byDocsDir.TryGetValue(resolution.DocsDir, out var owner))
                {
                    aliases[projectScope] = owner;
                    Log.Information(
                        "Knowledge scope {Scope} shares the base '{Owner}'", projectScope, owner);
                    continue;
                }

                // A shared base is named after the directory it lives in, so
                // five appkit repos read as one "appkit" rather than as
                // whichever member happened to be discovered first. Falls back
                // to the project name if that name belongs to something else.
                var scopeName = projectScope;
                if (resolution.IsRedirected &&
                    BaseNameFor(resolution.DocsDir) is { } folder &&
                    !taken.Contains(folder) &&
                    !reserved.Contains(folder))
                {
                    scopeName = folder;
                }
                else if (taken.Contains(scopeName))
                {
                    // Two scopes cannot share a name: registration is
                    // last-wins, so the loser's documents would be reachable
                    // under no name at all while every search using it silently
                    // read the winner's directory. A project folder called
                    // "global" is the case that matters — it would replace the
                    // shared base for every project in the tree.
                    var unique = scopeName;
                    for (var n = 2; taken.Contains(unique); n++)
                    {
                        unique = $"{scopeName}-{n}";
                    }

                    Log.Warning(
                        "Knowledge scope name '{Wanted}' is already in use; registering {Project} " +
                        "as '{Actual}'", scopeName, projectScope, unique);
                    scopeName = unique;
                }

                taken.Add(scopeName);
                aliases[projectScope] = scopeName;

                // Also under the scope's own name: a shared base is addressed
                // by that, and the hint has to know the documents are not in
                // the caller's repo whichever name the caller used.
                if (resolution.IsRedirected)
                {
                    redirected[scopeName] = resolution.DocsDir;
                }

                // A session exports FIREPIT_PROJECT_NAME from the configured
                // id when there is one, so an agent asks under a name the
                // registry has never heard of. The meta project is the live
                // case — folder `.firepit`, id `firepit-central` — and without
                // this every one of its searches carries a warning about its
                // own base not existing, which teaches the agent to ignore
                // warnings. Same defect 0.14.1 fixed in the project registry,
                // one layer down.
                // Never over an entry that already exists: a project's own
                // name always wins over another project's configured id.
                if (configuredId is { } id && !aliases.ContainsKey(id))
                {
                    aliases[id] = scopeName;
                }

                byDocsDir[resolution.DocsDir] = scopeName;
                registrations.Add(new KnowledgeScopeRegistration(
                    scopeName,
                    project.Path,
                    KnowledgeStoreLocation.For(project.Path, resolution.DocsDir)));
            }

            _brokenKnowledgeScopes = broken;
            _redirectedKnowledgeScopes = redirected;
            _knowledgeScopeAliases = aliases;
            svc.SyncScopes(registrations);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Knowledge scope sync failed");
        }
    }

    /// <summary>
    /// What to call a shared base, given the directory holding its docs.
    /// </summary>
    /// <remarks>
    /// The conventional layout ends in the fixed folder name — <c>appkit/knowledge</c>
    /// — so the leaf says nothing and the level above carries the meaning. A
    /// directory the user named themselves is taken as-is.
    /// </remarks>
    /// <summary>The project's configured id, when it differs from its folder name.</summary>
    private string? ConfiguredId(Firepit.Core.Projects.Project project)
    {
        try
        {
            var id = _projectConfigStore.Load(project.Path)?.Id;
            return string.IsNullOrWhiteSpace(id) ||
                   string.Equals(id, project.Name, StringComparison.OrdinalIgnoreCase)
                       ? null
                       : id;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the project id for {Project}", project.Name);
            return null;
        }
    }

    private static string? BaseNameFor(string docsDir)
    {
        var trimmed = docsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (!string.Equals(leaf, KnowledgeLayout.DirName, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(leaf) ? null : leaf;
        }

        var parentDir = Path.GetDirectoryName(trimmed) ?? string.Empty;
        var parent = Path.GetFileName(parentDir);

        // One more level when the parent is the .firepit folder: repos sharing
        // another repo's own base resolve to <repo>/.firepit/knowledge, whose
        // meaningful segment is the repo, not the fixed folder name. Without
        // this the shared base is called ".firepit" — which collides with the
        // meta project's folder name, gets rejected as reserved, and falls back
        // to whichever member happened to be registered first.
        if (string.Equals(parent, ".firepit", StringComparison.OrdinalIgnoreCase))
        {
            var owner = Path.GetFileName(Path.GetDirectoryName(parentDir) ?? string.Empty);
            return string.IsNullOrEmpty(owner) ? null : owner;
        }

        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    // The digest stays in the project (CLAUDE.md imports it from there) but is
    // compiled from docs that deliberately are not in this repo — so keep it
    // out of the repo too, locally and without a committed trace.
    private static void ExcludePinnedDigest(Firepit.Core.Projects.Project project, string docsDir)
    {
        try
        {
            if (Firepit.Core.Projects.GitLocalExclude.Ensure(
                    project.Path, Firepit.Core.Blueprints.FirepitBlueprintDefaults.PinnedDigestPath))
            {
                Log.Information(
                    "Excluded the pinned digest from git for {Project} (knowledge lives at {Dir})",
                    project.Name, docsDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update .git/info/exclude for {Project}", project.Name);
        }
    }

    /// <summary>
    /// Per-project settings. Lives here because the only thing it configures
    /// so far is the knowledge location, and applying it means re-registering
    /// scopes.
    /// </summary>
    private void OpenProjectSettings(Views.SessionTab session)
    {
        var metaPath = Path.GetFullPath(Path.Combine(_settings.ProjectsRoot, ".firepit"));
        var dialog = new Views.ProjectSettingsDialog(
            session.Context.Name, session.Context.Path, metaPath)
        {
            Owner = this,
        };
        Views.DialogSizing.Apply(dialog, Views.ProjectSettingsDialog.DesignWidth);

        if (dialog.ShowDialog() == true && dialog.KnowledgeChanged)
        {
            SyncKnowledgeScopes();
            Log.Information(
                "Knowledge location changed for {Project}", session.Context.Name);
        }
    }

    private void DisposeKnowledgeService()
    {
        try { _knowledgeService?.Dispose(); } catch { /* ignored */ }
        _knowledgeService = null;
        try { _knowledgeLoggerFactory?.Dispose(); } catch { /* ignored */ }
        _knowledgeLoggerFactory = null;
    }

    /// <summary>
    /// Turns whatever a caller says into the scope actually registered: a
    /// session inside the meta project calls its scope "global", and a project
    /// sharing a base addresses it by its own name.
    /// </summary>
    private string MapToScopeName(string projectOrScopeName) =>
        _knowledgeScopeAliases.TryGetValue(projectOrScopeName, out var scope)
            ? scope
            : projectOrScopeName;

    /// <summary>
    /// What to tell the agent about persisting a knowledge file it just wrote.
    /// A redirected scope's docs live in another project, so the usual "commit
    /// it" would send the agent looking for a change this repo does not have.
    /// </summary>
    private string SaveHint(string scopeName, string wrote)
    {
        // Looked up under both the name the caller used and the scope it maps
        // to. A redirected repo with a configured id used to miss here and get
        // told "remember to commit the file" — instructing the agent to commit
        // research into the public repository the pointer exists to keep it
        // out of.
        return TryRedirect(scopeName, out var dir) || TryRedirect(MapToScopeName(scopeName), out dir)
            ? $"{wrote} Stored at {dir}, outside this repo — commit it there."
            : $"{wrote} Remember to commit the file.";

        bool TryRedirect(string name, out string target) =>
            _redirectedKnowledgeScopes.TryGetValue(name, out target!);
    }

    /// <summary>Why a scope is missing, when it is missing on purpose.</summary>
    private string? BrokenScopeReason(string? scopeName)
    {
        if (scopeName is null)
        {
            return null;
        }

        if (!_brokenKnowledgeScopes.TryGetValue(scopeName, out var reason) &&
            !_brokenKnowledgeScopes.TryGetValue(MapToScopeName(scopeName), out reason))
        {
            return null;
        }

        return $"Knowledge is disabled for '{scopeName}': {reason}";
    }

    // --- IMcpBackend knowledge members -----------------------------------

    public async Task<Firepit.Mcp.KnowledgeSearchResult> SearchKnowledgeAsync(
        string? projectScopeName, string scope, string query, int limit)
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return new Firepit.Mcp.KnowledgeSearchResult(
                false, "Knowledge service is not running", [], false);
        }

        if (BrokenScopeReason(projectScopeName) is { } broken)
        {
            return new Firepit.Mcp.KnowledgeSearchResult(false, broken, [], false);
        }

        var scopes = new List<string>();
        var project = projectScopeName is null ? null : MapToScopeName(projectScopeName);
        switch (scope.ToLowerInvariant())
        {
            case "global":
                scopes.Add(KnowledgeService.GlobalScopeName);
                break;
            case "project":
                if (string.IsNullOrEmpty(project))
                {
                    return new Firepit.Mcp.KnowledgeSearchResult(
                        false,
                        "No project context — pass projectName or use scope 'global'.",
                        [], false);
                }

                scopes.Add(project);
                break;
            default: // "both" (and anything unrecognised collapses to it)
                if (!string.IsNullOrEmpty(project))
                {
                    scopes.Add(project);
                }

                scopes.Add(KnowledgeService.GlobalScopeName);
                break;
        }

        try
        {
            var result = await svc.SearchAsync(query, scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), limit);
            var hits = result.Hits
                .Select(h => new KnowledgeHitInfo(
                    h.Scope, h.Path, h.Title, h.Heading, h.Snippet, Math.Round(h.Score, 4)))
                .ToArray();

            // Warnings lead. Zero hits from a base that was not searched reads
            // exactly like zero hits from a base that holds nothing, and the
            // agent acts on the second reading — so the caveat has to arrive
            // with the result, not sit in a log file.
            var notes = new List<string>();
            if (result.Warnings is { Count: > 0 })
            {
                notes.AddRange(result.Warnings);
                if (hits.Length == 0)
                {
                    notes.Add(
                        "No results — but for the reason(s) above, treat this as unknown rather than as nothing.");
                }
            }

            if (result.Degraded)
            {
                notes.Add(
                    "Vector search unavailable (embedding model not ready) — results are full-text only.");
            }

            return new Firepit.Mcp.KnowledgeSearchResult(
                true,
                notes.Count > 0 ? string.Join(" ", notes) : null,
                hits,
                result.Degraded);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_knowledge_search failed");
            return new Firepit.Mcp.KnowledgeSearchResult(false, ex.Message, [], false);
        }
    }

    public async Task<KnowledgeDocumentResult> GetKnowledgeDocumentAsync(string scopeName, string path)
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return new KnowledgeDocumentResult(false, "Knowledge service is not running");
        }

        if (BrokenScopeReason(scopeName) is { } broken)
        {
            return new KnowledgeDocumentResult(false, broken);
        }

        try
        {
            var doc = await svc.GetDocumentAsync(MapToScopeName(scopeName), path);
            return doc is null
                ? new KnowledgeDocumentResult(false, $"No document '{path}' in scope '{scopeName}'.")
                : new KnowledgeDocumentResult(true, null, doc.Scope, doc.Path, doc.Title, doc.Content);
        }
        catch (ArgumentException ex)
        {
            return new KnowledgeDocumentResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_knowledge_get failed");
            return new KnowledgeDocumentResult(false, ex.Message);
        }
    }

    public async Task<KnowledgeDocumentResult> AddKnowledgeDocumentAsync(
        string scopeName, string title, string content, bool pinned)
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return new KnowledgeDocumentResult(false, "Knowledge service is not running");
        }

        if (BrokenScopeReason(scopeName) is { } broken)
        {
            return new KnowledgeDocumentResult(false, broken);
        }

        try
        {
            var doc = await svc.AddDocumentAsync(MapToScopeName(scopeName), title, content, pinned);
            var message = SaveHint(
                scopeName,
                pinned
                    ? "Saved, indexed and pinned (auto-injected at session start)."
                    : "Saved and indexed.");
            return new KnowledgeDocumentResult(
                true, message, doc.Scope, doc.Path, doc.Title, doc.Content);
        }
        catch (ArgumentException ex)
        {
            return new KnowledgeDocumentResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_knowledge_add failed");
            return new KnowledgeDocumentResult(false, ex.Message);
        }
    }

    public async Task<KnowledgeDocumentResult> UpdateKnowledgeDocumentAsync(
        string scopeName, string path, string content, string? title, bool? pinned)
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return new KnowledgeDocumentResult(false, "Knowledge service is not running");
        }

        if (BrokenScopeReason(scopeName) is { } broken)
        {
            return new KnowledgeDocumentResult(false, broken);
        }

        try
        {
            var doc = await svc.UpdateDocumentAsync(MapToScopeName(scopeName), path, content, title, pinned);
            return doc is null
                ? new KnowledgeDocumentResult(
                    false,
                    $"No document '{path}' in scope '{scopeName}' — use firepit_knowledge_add for new docs.")
                : new KnowledgeDocumentResult(
                    true, SaveHint(scopeName, "Replaced and re-indexed."),
                    doc.Scope, doc.Path, doc.Title, doc.Content);
        }
        catch (ArgumentException ex)
        {
            return new KnowledgeDocumentResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_knowledge_update failed");
            return new KnowledgeDocumentResult(false, ex.Message);
        }
    }

    public async Task<ToolCallResult> DeleteKnowledgeDocumentAsync(string scopeName, string path)
    {
        var svc = _knowledgeService;
        if (svc is null)
        {
            return new ToolCallResult(false, "Knowledge service is not running");
        }

        if (BrokenScopeReason(scopeName) is { } broken)
        {
            return new ToolCallResult(false, broken);
        }

        try
        {
            var deleted = await svc.DeleteDocumentAsync(MapToScopeName(scopeName), path);
            return deleted
                ? new ToolCallResult(true, SaveHint(scopeName, "Deleted and removed from the index."))
                : new ToolCallResult(false, $"No document '{path}' in scope '{scopeName}'.");
        }
        catch (ArgumentException ex)
        {
            return new ToolCallResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_knowledge_delete failed");
            return new ToolCallResult(false, ex.Message);
        }
    }
}
