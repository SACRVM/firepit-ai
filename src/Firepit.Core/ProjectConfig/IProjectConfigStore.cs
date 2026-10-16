namespace Firepit.Core.ProjectConfig;

/// <summary>
/// What reading a project's <c>.firepit/config.json</c> found: the config, or
/// that there is none, or that there is one that could not be read.
/// </summary>
public sealed record ProjectConfigRead(ProjectConfig? Config, bool Exists, string? Error = null)
{
    /// <summary>
    /// A file is there but did not read as a config. Whoever writes must stop
    /// here: writing would replace the user's file with what little was read.
    /// </summary>
    public bool IsUnreadable => Exists && Config is null;
}

public interface IProjectConfigStore
{
    ProjectConfigRead Read(string projectPath);

    /// <summary>The config, or null when there is none or it cannot be read.</summary>
    ProjectConfig? Load(string projectPath) => Read(projectPath).Config;

    void Save(string projectPath, ProjectConfig config);
}
