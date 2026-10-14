using System.Diagnostics;
using System.Text.Json;

namespace Firepit.Core.Desktop;

/// <summary>
/// One Code session the Claude desktop app keeps: what opening a project needs
/// to know of it.
/// </summary>
public sealed record ClaudeDesktopSession(string Id, string Cwd, bool IsArchived, long LastActivityAt);

/// <summary>What opening a project did, in words for a tool result or a terminal.</summary>
public sealed record ProjectOpenOutcome(bool Ok, string Message);

/// <summary>Opens a project in the Claude desktop app.</summary>
public interface IProjectOpener
{
    ProjectOpenOutcome Open(string projectName, string projectPath);
}

/// <summary>
/// Opens a project the way a person means "go to that project": its main
/// session, which is the most recently active one the desktop app keeps in the
/// project's own folder, or a new session there when it has none.
/// </summary>
/// <remarks>
/// <para>Two links the app handles itself: <c>claude://code/new?folder=…</c>,
/// which its own jump list uses for "new session in a folder", and
/// <c>claude://claude.ai/epitaxy/&lt;id&gt;</c>, the link it gives out to open a
/// session from outside.</para>
/// <para>Which sessions exist is read from the app's own records,
/// <c>&lt;config&gt;/Claude/claude-code-sessions/&lt;account&gt;/&lt;org&gt;/local_*.json</c>.
/// That format is the app's, not a contract: a record that cannot be read is
/// skipped, and with none readable a project simply gets a new session.</para>
/// </remarks>
public sealed class ClaudeDesktopOpener : IProjectOpener
{
    private readonly string _sessionsRoot;
    private readonly Func<string, bool> _launch;

    public ClaudeDesktopOpener(string? sessionsRoot = null, Func<string, bool>? launch = null)
    {
        _sessionsRoot = sessionsRoot ?? DefaultSessionsRoot;
        _launch = launch ?? LaunchUrl;
    }

    public static string DefaultSessionsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

    public ProjectOpenOutcome Open(string projectName, string projectPath)
    {
        var main = MainSessionFor(ReadSessions(_sessionsRoot), projectPath);
        var url = main is null ? NewSessionLink(projectPath) : OpenSessionLink(main.Id);
        if (!_launch(url))
        {
            return new ProjectOpenOutcome(false,
                $"Could not hand {url} to the desktop: is the Claude app installed and registered for claude:// links?");
        }

        return new ProjectOpenOutcome(true, main is null
            ? $"Started a new Claude Code session in {projectName}."
            : $"Opened {projectName}'s Claude Code session.");
    }

    /// <summary>
    /// The project's main session: not archived, started in the project's own
    /// folder (a session in a worktree below it is a side task, not the main
    /// one), most recently active first.
    /// </summary>
    public static ClaudeDesktopSession? MainSessionFor(IEnumerable<ClaudeDesktopSession> sessions, string projectPath)
    {
        var target = Normalise(projectPath);
        return sessions
            .Where(s => !s.IsArchived && string.Equals(Normalise(s.Cwd), target, PlatformText.Paths))
            .OrderByDescending(s => s.LastActivityAt)
            .FirstOrDefault();
    }

    public static string NewSessionLink(string folder) =>
        "claude://code/new?folder=" + Uri.EscapeDataString(folder);

    public static string OpenSessionLink(string sessionId) =>
        "claude://claude.ai/epitaxy/" + Uri.EscapeDataString(sessionId);

    /// <summary>Every session record under the app's sessions folder that reads as one.</summary>
    public static IReadOnlyList<ClaudeDesktopSession> ReadSessions(string root)
    {
        if (!Directory.Exists(root)) return [];
        var sessions = new List<ClaudeDesktopSession>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = Text(r, "sessionId");
                var cwd = Text(r, "cwd");
                if (id is null || cwd is null) continue;
                var archived = r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True;
                var last = r.TryGetProperty("lastActivityAt", out var l) && l.TryGetInt64(out var ms) ? ms : 0;
                sessions.Add(new ClaudeDesktopSession(id, cwd, archived, last));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // The app's file, half written or in a shape this does not know.
            }
        }
        return sessions;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Hands a URL to the desktop: xdg-open on Linux, the shell elsewhere.</summary>
    private static bool LaunchUrl(string url)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Start(new ProcessStartInfo(url) { UseShellExecute = true }, wait: false);
        }

        // Inside a systemd service (INVOCATION_ID is set) whatever xdg-open
        // starts would join the service's cgroup: a Claude app started that
        // way would be killed by the next restart of firepit.service. A
        // transient unit of its own keeps it apart.
        var psi = Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 }
            ? new ProcessStartInfo("systemd-run") { ArgumentList = { "--user", "--collect", "--quiet", "--", "xdg-open", url } }
            : new ProcessStartInfo("xdg-open") { ArgumentList = { url } };
        return Start(psi, wait: true);
    }

    private static bool Start(ProcessStartInfo psi, bool wait)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            if (!wait) return true;
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
