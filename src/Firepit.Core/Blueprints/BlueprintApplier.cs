using System.IO;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Blueprints;

/// <summary>
/// Conformance state of one project against one blueprint. Produced by
/// <see cref="BlueprintApplier.Check"/>; an empty check means conformant.
/// Blanket ignores are warnings, not pending actions — fixing them rewrites
/// user content, so it stays behind an explicit opt-in on apply.
/// </summary>
public sealed record BlueprintCheck(
    string ProjectPath,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> MissingGitignoreLines,
    IReadOnlyList<string> MissingClaudeMdSections,
    bool MissingProjectConfig,
    IReadOnlyList<string> BlanketIgnores)
{
    public bool Conformant =>
        MissingFiles.Count == 0 &&
        MissingGitignoreLines.Count == 0 &&
        MissingClaudeMdSections.Count == 0 &&
        !MissingProjectConfig;

    /// <summary>Human/agent-readable pending actions, one line each.</summary>
    public IReadOnlyList<string> DescribePending()
    {
        var pending = new List<string>();
        if (MissingProjectConfig)
        {
            pending.Add("create .firepit/config.json scaffold");
        }

        pending.AddRange(MissingFiles.Select(f => $"create file {f}"));
        pending.AddRange(MissingGitignoreLines.Select(l => $"add .gitignore line: {l}"));
        pending.AddRange(MissingClaudeMdSections.Select(m => $"append CLAUDE.md section ({m})"));
        return pending;
    }
}

public sealed record BlueprintApplyOutcome(
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The one operation over a blueprint: idempotent apply. "New project" and
/// "modernise an old project" are this same operation on different starting
/// states — apply on a conformant project is a no-op by construction.
/// </summary>
public static class BlueprintApplier
{
    public static BlueprintCheck Check(Blueprint blueprint, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        var missingFiles = blueprint.Files
            .Where(f => !File.Exists(TargetPath(projectPath, f)))
            .Select(f => f.RelativePath)
            .ToArray();

        var missingGitignore = ProjectScaffolding.GetMissingGitignoreEntries(
            projectPath, blueprint.GitignoreLines);

        var claudeMdPath = Path.Combine(projectPath, "CLAUDE.md");
        var claudeMd = File.Exists(claudeMdPath) ? File.ReadAllText(claudeMdPath) : string.Empty;
        var missingSections = blueprint.ClaudeMdSections
            .Where(s => !claudeMd.Contains(s.Marker, StringComparison.Ordinal))
            .Select(s => s.Marker)
            .ToArray();

        var missingConfig = blueprint.EnsureProjectConfig &&
            !File.Exists(ProjectConfigScaffold.GetConfigPath(projectPath));

        return new BlueprintCheck(
            ProjectPath: projectPath,
            MissingFiles: missingFiles,
            MissingGitignoreLines: missingGitignore,
            MissingClaudeMdSections: missingSections,
            MissingProjectConfig: missingConfig,
            BlanketIgnores: ProjectScaffolding.DetectBlanketIgnores(projectPath));
    }

    /// <param name="metaProjectPath">
    /// The central repo. When given, the shared CLAUDE.md fragments are seeded
    /// there if missing and the section's import tokens are resolved for this
    /// project — path relative or absolute as it needs to be, and the policy
    /// fragment matching the repo's visibility. Null skips the substitution,
    /// which leaves the tokens visible rather than writing a wrong path.
    /// </param>
    public static BlueprintApplyOutcome Apply(
        Blueprint blueprint,
        string projectPath,
        string projectId,
        bool fixBlanketIgnores = false,
        string? metaProjectPath = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentException.ThrowIfNullOrEmpty(projectId);

        var check = Check(blueprint, projectPath);
        var actions = new List<string>();
        var warnings = new List<string>();

        if (check.MissingProjectConfig)
        {
            ProjectConfigScaffold.EnsureScaffold(projectPath, projectId);
            actions.Add("created .firepit/config.json scaffold");
        }

        foreach (var rel in check.MissingFiles)
        {
            var file = blueprint.Files.First(f => f.RelativePath == rel);
            var target = TargetPath(projectPath, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.SourcePath, target);
            actions.Add($"created file {rel}");
        }

        if (check.MissingGitignoreLines.Count > 0)
        {
            ProjectScaffolding.EnsureGitignoreBlock(projectPath, blueprint.GitignoreLines);
            actions.AddRange(check.MissingGitignoreLines.Select(l => $"added .gitignore line: {l}"));
        }

        var resolveTokens = metaProjectPath is not null &&
            check.MissingClaudeMdSections.Any(m =>
                blueprint.ClaudeMdSections
                    .First(s => s.Marker == m).Content
                    .Contains(FirepitFragments.SharedToken, StringComparison.Ordinal));

        if (resolveTokens)
        {
            foreach (var seeded in FirepitFragments.EnsureSeeded(metaProjectPath!))
            {
                actions.Add($"seeded shared fragment {seeded}");
            }
        }

        foreach (var marker in check.MissingClaudeMdSections)
        {
            var section = blueprint.ClaudeMdSections.First(s => s.Marker == marker);
            var content = metaProjectPath is null
                ? section.Content
                : FirepitFragments.ResolveSection(section.Content, projectPath, metaProjectPath);
            ProjectScaffolding.EnsureClaudeMdSection(projectPath, section.Marker, content);
            actions.Add($"appended CLAUDE.md section ({marker})");
        }

        if (check.BlanketIgnores.Count > 0)
        {
            if (fixBlanketIgnores)
            {
                ProjectScaffolding.MigrateBlanketIgnores(projectPath);
                actions.AddRange(check.BlanketIgnores.Select(l => $"disabled blanket ignore: {l}"));
            }
            else
            {
                warnings.AddRange(check.BlanketIgnores.Select(l =>
                    $"blanket ignore '{l}' hides shared config — re-run apply with fixBlanketIgnores=true to disable it"));
            }
        }

        return new BlueprintApplyOutcome(actions, warnings);
    }

    private static string TargetPath(string projectPath, BlueprintFile file) =>
        Path.Combine(projectPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
