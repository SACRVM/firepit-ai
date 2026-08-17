using System.IO;
using System.Threading.Tasks;
using Firepit.Core.Blueprints;
using Firepit.Knowledge;
using Firepit.Mcp;
using Serilog;

namespace Firepit;

/// <summary>
/// The integrity check: one pass that verifies what Firepit maintains still
/// matches what is on disk.
/// </summary>
/// <remarks>
/// Everything else in the knowledge subsystem is event-driven — a watcher, a
/// debounce, a sweep on drift. That is how it keeps up, and it is also why it
/// needs this: every one of those can stop delivering without anything looking
/// wrong, and a search that answers from a stale index is indistinguishable
/// from one that answers correctly. This pass does not trust the machinery, it
/// looks.
///
/// Repair is deliberately asymmetric. The index, the pinned digest and the
/// watchers are derived from the markdown and can always be rebuilt, so
/// repairing them is safe and automatic. A CLAUDE.md is authored content, and
/// rewriting it silently is not something a maintenance command should do —
/// those come back as findings with the fix named, for a human or an agent to
/// carry out.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Top-level keys in <c>.firepit/config.json</c> that Firepit does not
    /// read. Deliberately top-level only — deep validation would report on
    /// comments and forward-compatible additions, and the failure this catches
    /// is a whole section that silently does nothing.
    /// </summary>
    private static IReadOnlyList<string> UnknownConfigKeys(string projectPath)
    {
        var known = typeof(Firepit.Core.ProjectConfig.ProjectConfig)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var path = Path.Combine(projectPath, ".firepit", "config.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            return node is System.Text.Json.Nodes.JsonObject obj
                ? [.. obj.Select(kv => kv.Key).Where(k => !known.Contains(k)).Order()]
                : [];
        }
        catch (Exception)
        {
            // Unparseable config is its own problem and shows up elsewhere.
            return [];
        }
    }

    public async Task<IntegrityCheckResult> CheckIntegrityAsync(string? projectName, bool repair)
    {
        try
        {
            var (root, projects) = await SnapshotProjectsAsync();
            var metaPath = Path.GetFullPath(Path.Combine(root, ".firepit"));

            var targets = projectName is null
                ? projects
                : projects.Where(p =>
                    string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (targets.Length == 0)
            {
                return new IntegrityCheckResult(
                    false,
                    projectName is null
                        ? "No projects known."
                        : $"Unknown project: {projectName}",
                    []);
            }

            var svc = _knowledgeService;
            var blueprint = await Task.Run(() =>
            {
                var store = new BlueprintStore(root);
                if (!store.MetaProjectExists)
                {
                    return null;
                }

                store.EnsureDefaults();
                return store.TryLoad(FirepitBlueprintDefaults.DefaultBlueprintName);
            });

            var scopeByProject = await OnDispatcherAsync(() =>
                targets.ToDictionary(
                    p => p.Name,
                    p => MapToScopeName(p.Name),
                    StringComparer.OrdinalIgnoreCase));

            var brokenScopes = _brokenKnowledgeScopes;
            var results = new List<ProjectIntegrity>();

            // The pre-split layout is not drift in one project, it disables a
            // feature for every project: while the meta repo's own
            // .firepit/knowledge doubles as the global base, the meta project
            // gets no scope of its own, so a pointer aimed at it resolves to
            // nothing. Firepit already logs this. A log line nobody reads is
            // exactly the silent state this command exists to end.
            var legacyGlobal = KnowledgeLayout.UsesLegacyGlobal(metaPath);

            foreach (var project in targets)
            {
                var findings = new List<IntegrityFinding>();
                var repairs = new List<string>();

                // --- knowledge -------------------------------------------
                var scopeName = scopeByProject[project.Name];
                if (brokenScopes.TryGetValue(project.Name, out var brokenReason))
                {
                    findings.Add(new IntegrityFinding(
                        "error", "knowledge",
                        $"Knowledge is disabled for this project: {brokenReason}",
                        "fix .firepit/knowledge, or open Project settings and pick a location"));
                }
                else if (svc is not null)
                {
                    var scopes = await svc.CheckIntegrityAsync([scopeName], repair);
                    foreach (var scope in scopes)
                    {
                        repairs.AddRange(scope.Repairs ?? []);
                        foreach (var problem in scope.Describe())
                        {
                            // Documents that exist but cannot be found are the
                            // failure this whole command is for: the search says
                            // nothing, and nothing about the answer admits it.
                            var severity =
                                scope.MissingFromIndex.Count > 0 ||
                                scope.OutOfDate.Count > 0 ||
                                scope.IndexError is not null
                                    ? "error"
                                    : "warning";
                            findings.Add(new IntegrityFinding(
                                severity, "knowledge", problem,
                                repair ? null : "re-run with repair=true"));
                        }
                    }
                }

                if (legacyGlobal && string.Equals(
                        Path.GetFullPath(project.Path), metaPath, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new IntegrityFinding(
                        "error", "knowledge",
                        "Global knowledge is still at the pre-split path " +
                        $"({KnowledgeLayout.LocalDocsDir(metaPath)}), where it doubles as this " +
                        "project's own knowledge. While that is the case the meta project has no " +
                        "scope of its own, so any project pointing its .firepit/knowledge here " +
                        "finds no base at all.",
                        $"move the documents to {KnowledgeLayout.GlobalDocsDir(metaPath)} and reload"));
                }

                // --- project config --------------------------------------
                foreach (var unknown in await Task.Run(() => UnknownConfigKeys(project.Path)))
                {
                    // A key Firepit does not read is a setting the author
                    // believes is in force. knowledge.storage was exactly this:
                    // documented in one release, replaced in the next, and
                    // ignored in silence by every version after.
                    findings.Add(new IntegrityFinding(
                        "warning", "config",
                        $".firepit/config.json has an unknown key '{unknown}' — Firepit ignores it.",
                        "remove it, or check the spelling against the current config format"));
                }

                // --- blueprint -------------------------------------------
                if (blueprint is not null)
                {
                    var check = await Task.Run(() => BlueprintApplier.Check(blueprint, project.Path));
                    foreach (var pending in check.DescribePending())
                    {
                        findings.Add(new IntegrityFinding(
                            "warning", "blueprint", pending,
                            $"firepit_blueprint_apply(projectName: \"{project.Name}\")"));
                    }

                    foreach (var blanket in check.BlanketIgnores)
                    {
                        findings.Add(new IntegrityFinding(
                            "warning", "blueprint",
                            $"blanket ignore '{blanket}' hides shared config",
                            "firepit_blueprint_apply with fixBlanketIgnores=true"));
                    }
                }

                // --- shared fragments ------------------------------------
                foreach (var fragment in await Task.Run(
                    () => FragmentIntegrity.Check(project.Path, metaPath)))
                {
                    findings.Add(new IntegrityFinding(
                        fragment.Severity, "fragments", fragment.Message, fragment.Fix));
                }

                results.Add(new ProjectIntegrity(
                    project.Name, findings.Count == 0, findings, repairs));
            }

            var errors = results.Sum(r => r.Findings.Count(f => f.Severity == "error"));
            var warnings = results.Sum(r => r.Findings.Count(f => f.Severity == "warning"));
            var repaired = results.Sum(r => r.Repairs.Count);
            Log.Information(
                "Integrity check: {Projects} project(s), {Errors} error(s), {Warnings} warning(s), {Repairs} repair(s)",
                results.Count, errors, warnings, repaired);

            var message = errors == 0 && warnings == 0
                ? $"All {results.Count} project(s) sound." +
                  (repaired > 0 ? $" {repaired} repair(s) applied." : string.Empty)
                : $"{errors} error(s), {warnings} warning(s) across {results.Count} project(s)." +
                  (repaired > 0 ? $" {repaired} repair(s) applied." : string.Empty);

            return new IntegrityCheckResult(true, message, results);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_integrity_check failed");
            return new IntegrityCheckResult(false, ex.Message, []);
        }
    }
}
