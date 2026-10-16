using System.IO;
using Firepit.Core.Settings;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// One-shot migration: existing per-project entries in
/// <c>settings.Projects[]</c> that carry behavioural fields (quick-links,
/// MCP activations, agent overrides) get split out into
/// <c>&lt;projectPath&gt;/.firepit/config.json</c>. The global
/// <c>settings.json</c> entry is rewritten with only path + name preserved.
///
/// A <c>settings.json.bak</c> snapshot is taken before the global file is
/// rewritten. Idempotency is the caller's responsibility — typically gated
/// by a flag in <c>state.json</c>.
/// </summary>
public sealed class ProjectConfigMigrator
{
    private readonly IProjectConfigStore _projectStore;

    public ProjectConfigMigrator(IProjectConfigStore projectStore)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
    }

    /// <summary>
    /// Returns the migrated settings (or the input unchanged if nothing migrated)
    /// plus the count of projects that received a per-project file.
    /// </summary>
    public MigrationResult Migrate(FirepitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var projects = settings.Projects ?? [];
        if (projects.Count == 0)
        {
            return new MigrationResult(settings, MigratedCount: 0);
        }

        var migratedCount = 0;
        var newProjects = new List<ProjectSettings>(projects.Count);

        foreach (var project in projects)
        {
            if (!HasMigratableContent(project)
                || string.IsNullOrEmpty(project.Path)
                || !Directory.Exists(project.Path))
            {
                newProjects.Add(project);
                continue;
            }

            // Don't clobber an existing per-project file — assume the user
            // (or a prior run) already curated it. Migration just strips the
            // duplicated fields from settings.json in that case.
            // Exists, not Load: a file that is there but does not read is the
            // user's too, and writing would replace it with settings leftovers.
            if (!_projectStore.Read(project.Path).Exists)
            {
                _projectStore.Save(project.Path, BuildConfigFromSettings(project));
            }

            newProjects.Add(StripMigratableFields(project));
            migratedCount++;
        }

        if (migratedCount == 0)
        {
            return new MigrationResult(settings, MigratedCount: 0);
        }

        return new MigrationResult(settings with { Projects = newProjects }, migratedCount);
    }

    /// <summary>
    /// Convenience: writes a <c>.bak</c> snapshot of the existing settings file
    /// before the new content is saved. Caller still saves the result via the
    /// normal store API. No-op if the file doesn't exist yet.
    /// </summary>
    public static void BackupSettingsFile(string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(settingsPath);
        if (!File.Exists(settingsPath))
        {
            return;
        }
        try
        {
            File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
        }
        catch (IOException)
        {
            // Backup is best-effort — proceed with the migration regardless.
        }
    }

    private static bool HasMigratableContent(ProjectSettings project) =>
        (project.QuickLinks is { Count: > 0 })
        || (project.McpServers is { Count: > 0 })
        || !string.IsNullOrEmpty(project.AgentCommand)
        || (project.AgentArgs is { Count: > 0 });

    private static ProjectConfig BuildConfigFromSettings(ProjectSettings project) => new(
        Version:        1,
        QuickLinks:     project.QuickLinks?.Select(MapQuickLink).ToArray(),
        McpActivations: project.McpServers?.Select(id => MapActivation(id, project)).ToArray(),
        Agent:          BuildAgentConfig(project));

    private static ProjectQuickLink MapQuickLink(QuickLinkSettings q) => new(
        Name:     q.Name,
        Url:      q.Url,
        Target:   q.Target,
        Icon:     q.Icon,
        Disabled: q.Disabled);

    private static ProjectMcpActivation MapActivation(string id, ProjectSettings project)
    {
        var ov = project.McpOverrides is not null
                 && project.McpOverrides.TryGetValue(id, out var resolved)
                 ? resolved
                 : null;
        return new ProjectMcpActivation(
            Id:              id,
            ArgOverrides:    ov?.Args,
            EnvOverrides:    ov?.Environment,
            HeaderOverrides: ov?.Headers);
    }

    private static ProjectAgentConfig? BuildAgentConfig(ProjectSettings project)
    {
        if (string.IsNullOrEmpty(project.AgentCommand) && (project.AgentArgs is null || project.AgentArgs.Count == 0))
        {
            return null;
        }
        return new ProjectAgentConfig(
            Command: project.AgentCommand,
            Args:    project.AgentArgs);
    }

    private static ProjectSettings StripMigratableFields(ProjectSettings project) => new(
        Name:         project.Name,
        Path:         project.Path,
        AgentCommand: null,
        AgentArgs:    null,
        McpServers:   null,
        McpOverrides: null,
        QuickLinks:   null);
}

public sealed record MigrationResult(FirepitSettings Settings, int MigratedCount);
