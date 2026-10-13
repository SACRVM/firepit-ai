using Firepit.Core.Projects;
using Firepit.Mcp;

namespace Firepit.Workspace;

/// <summary>
/// What the workspace needs from a shell that hosts agent sessions in tabs.
/// The workspace owns the data; the shell owns the sessions. Without a shell
/// (<see cref="HeadlessTabHost"/>) the tab tools say so instead of failing
/// obscurely, and everything that is about files keeps working.
/// </summary>
/// <remarks>
/// Implementations are called from thread-pool threads and marshal onto
/// their own UI thread where they need one.
/// </remarks>
public interface ITabHost
{
    /// <summary>The sessions the shell has open; empty without a shell.</summary>
    IReadOnlyList<SessionInfo> ListSessions();

    /// <summary>The state of the project's session, or null when it has none.</summary>
    string? SessionState(string projectPath);

    Task<ToolCallResult> OpenAsync(Project project, bool resume);

    Task<ToolCallResult> FocusAsync(Project project);

    Task<ToolCallResult> CloseAsync(Project project);

    /// <summary>
    /// Hands a project's reloaded config to its open tab. The workspace has
    /// already re-read the config and re-registered the knowledge scopes.
    /// </summary>
    Task<ToolCallResult> ReloadAsync(Project project, Core.ProjectConfig.ProjectConfig? config, bool restart);

    /// <summary>
    /// A config the workspace itself just wrote. Pushed straight to the open
    /// tab: Firepit's own writes must not depend on the file watcher, which
    /// exists for edits made outside Firepit.
    /// </summary>
    void ApplyConfig(string projectPath, Core.ProjectConfig.ProjectConfig config);

    /// <summary>The project's artifacts.json changed.</summary>
    void RefreshArtifacts(string projectPath);

    /// <summary>Something the user should see now; a log line elsewhere.</summary>
    void Notify(string message, bool isError);
}

/// <summary>
/// No shell: Firepit runs as a daemon behind Claude Code, and sessions are
/// Claude Code's. The tab tools answer that plainly.
/// </summary>
public sealed class HeadlessTabHost : ITabHost
{
    private const string NoShell =
        "No Firepit shell is running — sessions live in Claude Code, so there is no tab to ";

    public IReadOnlyList<SessionInfo> ListSessions() => [];

    public string? SessionState(string projectPath) => null;

    public Task<ToolCallResult> OpenAsync(Project project, bool resume) =>
        Task.FromResult(new ToolCallResult(false, NoShell + $"open for {project.Name}."));

    public Task<ToolCallResult> FocusAsync(Project project) =>
        Task.FromResult(new ToolCallResult(false, NoShell + $"focus for {project.Name}."));

    public Task<ToolCallResult> CloseAsync(Project project) =>
        Task.FromResult(new ToolCallResult(false, NoShell + $"close for {project.Name}."));

    public Task<ToolCallResult> ReloadAsync(Project project, Core.ProjectConfig.ProjectConfig? config, bool restart) =>
        Task.FromResult(new ToolCallResult(true,
            $"Reloaded {project.Name}: config re-read, knowledge scopes re-registered" +
            (restart ? " (no session to restart — none is hosted by Firepit)." : ".")));

    public void ApplyConfig(string projectPath, Core.ProjectConfig.ProjectConfig config)
    {
    }

    public void RefreshArtifacts(string projectPath)
    {
    }

    public void Notify(string message, bool isError)
    {
        if (isError)
        {
            Serilog.Log.Warning("{Message}", message);
        }
        else
        {
            Serilog.Log.Information("{Message}", message);
        }
    }
}
