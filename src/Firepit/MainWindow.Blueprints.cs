using System.IO;
using System.Threading.Tasks;
using Firepit.Core.Blueprints;
using Firepit.Knowledge;
using Firepit.Mcp;
using Serilog;

namespace Firepit;

/// <summary>
/// IMcpBackend blueprint members (ROADMAP M9, blueprints half). Pattern:
/// snapshot the project list + projects root on the dispatcher, then do all
/// file work on the thread pool — blueprint operations never touch UI state.
/// </summary>
public partial class MainWindow
{
    private const string MetaProjectMissingMessage =
        "The .firepit meta project doesn't exist yet — create it via Firepit " +
        "(Set up Firepit central project) first; blueprints live inside it.";

    /// <summary>
    /// A public repository never keeps its knowledge base inside itself.
    /// Establishes the pointer before the blueprint runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The blueprint was taught not to <i>destroy</i> a pointer file. It was
    /// never taught to <i>create</i> one — so it went on giving every project a
    /// local <c>.firepit/knowledge/</c>, public repos included, and the policy
    /// fragment stated the rule conditionally ("if it is a pointer, that is
    /// already arranged") about an arrangement nothing made. Twelve public
    /// repos ended up holding a knowledge base, one of them with eleven
    /// documents pushed to a public remote.
    /// </para>
    /// <para>
    /// Running before the blueprint rather than inside it is deliberate: with
    /// the pointer in place the existing conformance guard already treats the
    /// local directory as inapplicable, so the rule needs no second
    /// implementation. It also keeps <c>Firepit.Core</c> free of a reference to
    /// <c>Firepit.Knowledge</c>, which is deliberately self-contained.
    /// </para>
    /// <para>
    /// A base that already holds documents is never moved silently — the
    /// documents are in that repository's history, and taking them out is a
    /// decision with consequences this call cannot weigh. It reports instead.
    /// </para>
    /// </remarks>
    private static (string? Action, string? Warning) EnsureKnowledgeIsHostedIfPublic(
        string projectPath, string projectName, string metaProjectPath)
    {
        try
        {
            if (Firepit.Core.ProjectConfig.KnowledgeRedirect.IsRedirected(projectPath))
            {
                return (null, null);
            }

            // Detect, not Inspect: an unreadable visibility falls back to
            // Public, and that is the safe direction here. Hosting a private
            // repo's knowledge costs nothing and is reversible in Project
            // settings; leaving a public repo's knowledge inside it is not.
            if (Firepit.Core.Projects.GitHubVisibility.Detect(projectPath)
                != Firepit.Core.Projects.RepoVisibility.Public)
            {
                return (null, null);
            }

            var local = KnowledgeLayout.LocalDocsDir(projectPath);
            var hosted = KnowledgeLayout.HostedDocsDir(metaProjectPath, projectName);

            if (Directory.Exists(local))
            {
                // README.md is the blueprint's own seed and regenerates; it is
                // not content and does not travel to the hosted store.
                //
                // Counts *.md, matching KnowledgePlacement.Check. Counting every
                // file here would refuse over a stray non-document that the
                // check calls clean — the two would disagree about the same
                // directory, and the move that resolves it takes anything else
                // along anyway.
                var readme = Path.Combine(local, "README.md");
                var documents = Directory
                    .EnumerateFiles(local, "*.md", SearchOption.AllDirectories)
                    .Where(f => !string.Equals(f, readme, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (documents.Count > 0)
                {
                    return (null,
                        $"{documents.Count} knowledge document(s) live inside this public repo " +
                        $"({local}). Blueprint apply will not move documents — open Project " +
                        $"settings and host them at {hosted}, then commit the removal here.");
                }

                if (File.Exists(readme))
                {
                    File.Delete(readme);
                }
            }

            KnowledgePointerFile.Apply(projectPath, hosted);
            Log.Information(
                "Knowledge for public repo {Project} is hosted at {Dir}", projectName, hosted);
            return ($"public repo — knowledge hosted at {hosted}", null);
        }
        catch (Exception ex)
        {
            // Never fail an apply over this: the blueprint's other work is
            // still worth doing, and the integrity check reports the state.
            Log.Warning(ex, "Could not host knowledge for {Project}", projectName);
            return (null, $"could not host this public repo's knowledge outside it: {ex.Message}");
        }
    }

    public async Task<BlueprintListResult> ListBlueprintsAsync()
    {
        try
        {
            var root = await OnDispatcherAsync(() => _settings.ProjectsRoot);
            return await Task.Run(() =>
            {
                var store = new BlueprintStore(root);
                if (!store.MetaProjectExists)
                {
                    return new BlueprintListResult(false, MetaProjectMissingMessage, []);
                }

                store.EnsureDefaults();
                var blueprints = store.LoadAll()
                    .Select(b => new BlueprintInfo(
                        b.Name,
                        b.Description,
                        b.Files.Select(f => f.RelativePath).ToArray(),
                        b.GitignoreLines,
                        b.ClaudeMdSections.Select(s => s.Marker).ToArray(),
                        b.EnsureProjectConfig))
                    .ToArray();
                return new BlueprintListResult(true, null, blueprints);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_blueprint_list failed");
            return new BlueprintListResult(false, ex.Message, []);
        }
    }

    public async Task<BlueprintCheckResult> CheckBlueprintAsync(string? projectName, string blueprintName)
    {
        try
        {
            var (root, projects) = await SnapshotProjectsAsync();
            return await Task.Run(() =>
            {
                var store = new BlueprintStore(root);
                if (!store.MetaProjectExists)
                {
                    return new BlueprintCheckResult(false, MetaProjectMissingMessage, blueprintName, []);
                }

                store.EnsureDefaults();
                var blueprint = store.TryLoad(blueprintName);
                if (blueprint is null)
                {
                    return new BlueprintCheckResult(
                        false, $"Unknown blueprint: {blueprintName}", blueprintName, []);
                }

                var targets = projectName is null
                    ? projects
                    : projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (targets.Length == 0)
                {
                    return new BlueprintCheckResult(
                        false,
                        projectName is null ? "No projects known." : $"Unknown project: {projectName}",
                        blueprintName, []);
                }

                var checks = targets
                    .Select(p =>
                    {
                        var check = BlueprintApplier.Check(blueprint, p.Path);
                        return new BlueprintProjectCheck(
                            p.Name,
                            check.Conformant,
                            check.DescribePending(),
                            check.BlanketIgnores
                                .Select(l => $"blanket ignore '{l}' hides shared config")
                                .ToArray());
                    })
                    .ToArray();
                return new BlueprintCheckResult(true, null, blueprintName, checks);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_blueprint_check failed");
            return new BlueprintCheckResult(false, ex.Message, blueprintName, []);
        }
    }

    public async Task<BlueprintApplyResult> ApplyBlueprintAsync(
        string projectName, string blueprintName, bool fixBlanketIgnores)
    {
        try
        {
            var (root, projects) = await SnapshotProjectsAsync();
            return await Task.Run(() =>
            {
                var store = new BlueprintStore(root);
                if (!store.MetaProjectExists)
                {
                    return new BlueprintApplyResult(false, MetaProjectMissingMessage);
                }

                store.EnsureDefaults();
                var blueprint = store.TryLoad(blueprintName);
                if (blueprint is null)
                {
                    return new BlueprintApplyResult(false, $"Unknown blueprint: {blueprintName}");
                }

                var project = projects.FirstOrDefault(
                    p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
                if (project == default)
                {
                    return new BlueprintApplyResult(false, $"Unknown project: {projectName}");
                }

                // Before the blueprint, not inside it: with the pointer in
                // place the conformance guard already skips the local
                // knowledge directory.
                var (hostAction, hostWarning) = EnsureKnowledgeIsHostedIfPublic(
                    project.Path, project.Name, store.MetaProjectPath);

                var outcome = BlueprintApplier.Apply(
                    blueprint, project.Path, project.Name, fixBlanketIgnores,
                    // From the store, not the window: this runs off the
                    // dispatcher, and the store was built from the snapshotted
                    // root a few lines up.
                    metaProjectPath: store.MetaProjectPath);

                var actions = hostAction is null
                    ? outcome.Actions
                    : [hostAction, .. outcome.Actions];
                var warnings = hostWarning is null
                    ? outcome.Warnings
                    : [hostWarning, .. outcome.Warnings];

                Log.Information(
                    "Blueprint '{Blueprint}' applied to {Project}: {Count} action(s)",
                    blueprintName, project.Name, actions.Count);
                var message = actions.Count == 0
                    ? "Already conformant — nothing to do."
                    : null;
                return new BlueprintApplyResult(
                    true, message, project.Name, blueprintName, actions, warnings);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "firepit_blueprint_apply failed");
            return new BlueprintApplyResult(false, ex.Message);
        }
    }

    private Task<(string Root, (string Name, string Path)[] Projects)> SnapshotProjectsAsync() =>
        OnDispatcherAsync(() =>
            (_settings.ProjectsRoot,
             _allProjects.Select(p => (p.Name, p.Path)).ToArray()));
}
