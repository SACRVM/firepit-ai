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

    // Scopes that asked for a store we could not resolve, with the reason.
    // Kept so the knowledge tools can answer "why is my scope gone" instead of
    // the caller guessing from a bare not-found.
    private IReadOnlyDictionary<string, string> _brokenKnowledgeScopes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Scope name → the project whose store holds its docs. Only redirected
    // scopes appear here, and only so the tools stop telling an agent to
    // commit a file that deliberately is not in this repo.
    private IReadOnlyDictionary<string, string> _redirectedKnowledgeScopes =
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
            var byName = _allProjects
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);

            var registrations = new List<KnowledgeScopeRegistration>();
            var broken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var redirected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in _allProjects)
            {
                var isMeta = string.Equals(
                    Path.GetFullPath(project.Path), metaPath, StringComparison.OrdinalIgnoreCase);
                if (isMeta)
                {
                    _metaProjectName = project.Name;
                }

                var name = isMeta ? KnowledgeService.GlobalScopeName : project.Name;
                if (!seen.Add(name))
                {
                    continue;
                }

                var (location, error) = ResolveKnowledgeStore(project, byName, redirected, name);
                if (error is not null)
                {
                    // Deliberately not registered. A scope that silently fell
                    // back to the project's own folder would put the research
                    // this setting exists to hide into the repo it hides it
                    // from — better the tools report the scope as missing.
                    broken[name] = error;
                    Log.Error(
                        "Knowledge scope {Scope} disabled: {Reason}", name, error);
                    continue;
                }

                registrations.Add(new KnowledgeScopeRegistration(name, project.Path, location));
            }

            _brokenKnowledgeScopes = broken;
            _redirectedKnowledgeScopes = redirected;
            svc.SyncScopes(registrations);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Knowledge scope sync failed");
        }
    }

    /// <summary>
    /// Where one project's knowledge docs belong, per its
    /// <c>knowledge.storage</c> setting. Returns exactly one of a location or
    /// an error — never a quiet default when the setting names a project we
    /// cannot resolve.
    /// </summary>
    private (KnowledgeStoreLocation? Location, string? Error) ResolveKnowledgeStore(
        Firepit.Core.Projects.Project project,
        IReadOnlyDictionary<string, string> projectsByName,
        IDictionary<string, string> redirected,
        string scopeName)
    {
        string? storage = null;
        try
        {
            storage = _projectConfigStore.Load(project.Path)?.Knowledge?.Storage;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read knowledge config for {Project}", project.Name);
        }

        if (string.IsNullOrWhiteSpace(storage) ||
            string.Equals(storage, ProjectKnowledgeConfig.RepoStorage, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (string.Equals(storage, project.Name, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!projectsByName.TryGetValue(storage, out var storePath))
        {
            return (null,
                $"knowledge.storage names '{storage}', which is not a project Firepit knows. " +
                "Use a project name from firepit_list_projects, or \"repo\".");
        }

        var location = KnowledgeStoreLocation.InStore(storePath, project.Name, project.Path);
        redirected[scopeName] = storage;

        // The digest still lands in the project (CLAUDE.md imports it from
        // there) but it is compiled from docs that are deliberately not in
        // this repo — so keep it out of the repo too, locally and silently.
        try
        {
            if (Firepit.Core.Projects.GitLocalExclude.Ensure(
                    project.Path, Firepit.Core.Blueprints.FirepitBlueprintDefaults.PinnedDigestPath))
            {
                Log.Information(
                    "Excluded the pinned digest from git for {Project} (knowledge stored in '{Store}')",
                    project.Name, storage);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update .git/info/exclude for {Project}", project.Name);
        }

        return (location, null);
    }

    private void DisposeKnowledgeService()
    {
        try { _knowledgeService?.Dispose(); } catch { /* ignored */ }
        _knowledgeService = null;
        try { _knowledgeLoggerFactory?.Dispose(); } catch { /* ignored */ }
        _knowledgeLoggerFactory = null;
    }

    /// <summary>A session inside the meta project calls its scope "global".</summary>
    private string MapToScopeName(string projectOrScopeName) =>
        _metaProjectName is not null &&
        string.Equals(projectOrScopeName, _metaProjectName, StringComparison.OrdinalIgnoreCase)
            ? KnowledgeService.GlobalScopeName
            : projectOrScopeName;

    /// <summary>
    /// What to tell the agent about persisting a knowledge file it just wrote.
    /// A redirected scope's docs live in another project, so the usual "commit
    /// it" would send the agent looking for a change this repo does not have.
    /// </summary>
    private string SaveHint(string scopeName, string wrote)
    {
        var scope = MapToScopeName(scopeName);
        return _redirectedKnowledgeScopes.TryGetValue(scope, out var store)
            ? $"{wrote} Stored in the '{store}' project, not this repo — commit it there."
            : $"{wrote} Remember to commit the file.";
    }

    /// <summary>Why a scope is missing, when it is missing on purpose.</summary>
    private string? BrokenScopeReason(string? scopeName) =>
        scopeName is not null &&
        _brokenKnowledgeScopes.TryGetValue(MapToScopeName(scopeName), out var reason)
            ? $"Knowledge is disabled for '{scopeName}': {reason}"
            : null;

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
            var message = result.Degraded
                ? "Vector search unavailable (embedding model not ready) — results are full-text only."
                : null;
            return new Firepit.Mcp.KnowledgeSearchResult(true, message, hits, result.Degraded);
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
