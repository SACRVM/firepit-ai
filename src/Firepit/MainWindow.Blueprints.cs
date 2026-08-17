using System.Threading.Tasks;
using Firepit.Core.Blueprints;
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

                var outcome = BlueprintApplier.Apply(
                    blueprint, project.Path, project.Name, fixBlanketIgnores,
                    // From the store, not the window: this runs off the
                    // dispatcher, and the store was built from the snapshotted
                    // root a few lines up.
                    metaProjectPath: store.MetaProjectPath);
                Log.Information(
                    "Blueprint '{Blueprint}' applied to {Project}: {Count} action(s)",
                    blueprintName, project.Name, outcome.Actions.Count);
                var message = outcome.Actions.Count == 0
                    ? "Already conformant — nothing to do."
                    : null;
                return new BlueprintApplyResult(
                    true, message, project.Name, blueprintName, outcome.Actions, outcome.Warnings);
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
