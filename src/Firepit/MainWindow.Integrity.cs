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

    /// <summary>
    /// Names to documents directory, tolerating what a project list can hold:
    /// two entries with the same name (a manual entry outside the root next to
    /// a discovered folder of that name). <c>ToDictionary</c> throws on that,
    /// and here it threw from inside the integrity check itself.
    /// </summary>
    private static Dictionary<string, string> ByName(
        IEnumerable<(string Name, string Dir)> pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in pairs)
        {
            map[name] = dir;
        }

        return map;
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
                ByName(targets.Select(p => (p.Name, Dir: MapToScopeName(p.Name)))));

            var brokenScopes = _brokenKnowledgeScopes;
            var results = new List<ProjectIntegrity>();

            // The pre-split layout is not drift in one project, it disables a
            // feature for every project: while the meta repo's own
            // .firepit/knowledge doubles as the global base, the meta project
            // gets no scope of its own, so a pointer aimed at it resolves to
            // nothing. Firepit already logs this. A log line nobody reads is
            // exactly the silent state this command exists to end.
            var legacyGlobal = KnowledgeLayout.UsesLegacyGlobal(metaPath);

            // A pointer may aim anywhere, which means it may aim inside another
            // scope's documents directory. Nothing in the locator can see that
            // — it resolves one project at a time — and the result is quiet
            // cross-contamination: the outer scope indexes the inner one's
            // files too, so a project's research becomes searchable from every
            // project that reads the outer base.
            // Built from every project, not just the ones being reported on:
            // an overlap is a relationship between two scopes, and checking a
            // single project must not answer differently just because the other
            // half of the pair was filtered out of the request.
            var docsDirs = await Task.Run(() => ByName(projects
                .Select(p => (p.Name, Resolution: KnowledgeLocator.Resolve(p.Path)))
                .Where(x => x.Resolution.Error is null)
                .Select(x => (x.Name, Dir: x.Resolution.DocsDir))));

            // Added under a name that does not overwrite a project which
            // happens to be called "global" — overwriting would drop that
            // project, or the shared base, out of the analysis entirely.
            var globalKey = docsDirs.ContainsKey(KnowledgeService.GlobalScopeName)
                ? KnowledgeService.GlobalScopeName + " (shared base)"
                : KnowledgeService.GlobalScopeName;
            docsDirs[globalKey] = KnowledgeLayout.ResolveGlobalDocsDir(metaPath);

            var overlaps = ScopeOverlaps.Find(docsDirs);

            foreach (var project in targets)
            {
                var findings = new List<IntegrityFinding>();
                var repairs = new List<string>();

                try
                {
                    // --- knowledge -------------------------------------------
                    var scopeName = scopeByProject[project.Name];
                    if (brokenScopes.TryGetValue(project.Name, out var brokenReason))
                    {
                        findings.Add(new IntegrityFinding(
                            "error", "knowledge",
                            $"Knowledge is disabled for this project: {brokenReason}",
                            "fix .firepit/knowledge, or open Project settings and pick a location"));
                    }
                    else if (svc is null)
                    {
                        // Silence here would report every project as sound while no
                        // knowledge exists at all — the check would confirm the
                        // health of a subsystem that never started.
                        findings.Add(new IntegrityFinding(
                            "error", "knowledge",
                            "The knowledge service is not running, so nothing about this project's " +
                            "knowledge could be verified and no search can answer from it.",
                            "check the log for the startup failure, then restart Firepit"));
                    }
                    else
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
                                    !scope.IsRegistered ||
                                    scope.MissingFromIndex.Count > 0 ||
                                    scope.OutOfDate.Count > 0 ||
                                    scope.IndexError is not null
                                        ? "error"
                                        : "warning";
                                findings.Add(new IntegrityFinding(
                                    severity, "knowledge", problem,
                                    scope.IsRegistered
                                        ? repair ? null : "re-run with repair=true"
                                        : "reload projects; if it persists, check the log for " +
                                          "'Knowledge scope sync failed'"));
                            }
                        }
                    }

                    var isMeta = string.Equals(
                        Path.GetFullPath(project.Path), metaPath, StringComparison.OrdinalIgnoreCase);

                    if (legacyGlobal && isMeta)
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

                    foreach (var overlap in overlaps.Where(o =>
                        string.Equals(o.Inner, project.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        findings.Add(new IntegrityFinding(
                            "error", "knowledge",
                            $"This project's knowledge directory ({overlap.InnerDir}) sits inside the " +
                            $"'{overlap.Outer}' base. Everything saved here is indexed into " +
                            $"'{overlap.Outer}' as well, so it is searchable from every project that " +
                            "reads it.",
                            $"point .firepit/knowledge at {KnowledgeLayout.HostedDocsDir(metaPath, project.Name)}"));
                    }

                    // Reported to the containing scope too. An overlap is a
                    // relationship, and checking only the nested half meant a base
                    // quietly absorbing another project's research was declared
                    // sound when asked about directly. The meta project answers for
                    // the shared base as well — that is the base most likely to be
                    // the outer half.
                    var answersFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        project.Name,
                    };
                    if (isMeta)
                    {
                        answersFor.Add(globalKey);
                    }

                    foreach (var overlap in overlaps.Where(o => answersFor.Contains(o.Outer)))
                    {
                        findings.Add(new IntegrityFinding(
                            "error", "knowledge",
                            $"'{overlap.Inner}' keeps its knowledge at {overlap.InnerDir}, inside this " +
                            $"project's base ({overlap.OuterDir}). Its documents are indexed here as " +
                            "well and answer searches made from this project.",
                            $"move it out of {overlap.OuterDir} — " +
                            $"{KnowledgeLayout.HostedDocsDir(metaPath, overlap.Inner)} is the usual place"));
                    }

                        // --- knowledge placement policy ----------------------
                    // Separate from the scope check above, which asks whether
                    // the index matches the documents. This asks whether the
                    // documents are in a defensible place at all — the question
                    // that stayed unasked while twelve public repos held a
                    // knowledge base and one of them pushed eleven documents.
                    foreach (var finding in await Task.Run(
                        () => KnowledgePlacement.Check(project.Path, project.Name, metaPath)))
                    {
                        findings.Add(new IntegrityFinding(
                            finding.Severity, "knowledge", finding.Message, finding.Fix));
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
                }
                catch (Exception ex)
                {
                    // One project must not cost the whole pass. A CLAUDE.md
                    // held open by an editor used to abort the run and return
                    // an empty result list — which reads as "nothing to
                    // report", the opposite of what happened.
                    Log.Warning(ex, "Integrity check failed for {Project}", project.Name);
                    findings.Add(new IntegrityFinding(
                        "error", "check",
                        $"This project could not be checked: {ex.Message}",
                        "resolve the error above and re-run; the other projects were still checked"));
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
