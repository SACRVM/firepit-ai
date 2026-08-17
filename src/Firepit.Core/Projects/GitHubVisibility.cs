using System.Diagnostics;
using System.IO;

namespace Firepit.Core.Projects;

/// <summary>
/// Whether a project's GitHub repository is public. Asked once, when a
/// blueprint is applied, to pick which policy fragment its CLAUDE.md imports.
/// </summary>
public enum RepoVisibility
{
    /// <summary>Not on GitHub — no git at all, or a repo with no GitHub remote.</summary>
    None,
    Public,
    Private,
}

public static class GitHubVisibility
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Which policy fragment applies. <see cref="RepoVisibility.None"/> means
    /// none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different unknowns hide behind "we could not tell", and they want
    /// opposite answers. A repo that is on GitHub but whose visibility we
    /// failed to read defaults to <see cref="RepoVisibility.Public"/>: the
    /// strict rule costs nothing if wrong, while guessing private invites
    /// research into a repository anyone can read.
    /// </para>
    /// <para>
    /// A project that is not on GitHub at all is not the same thing. There is
    /// no visibility to be careful about, and telling its agent "anything
    /// committed here is readable by anyone" is simply false. It gets no class
    /// fragment.
    /// </para>
    /// </remarks>
    public static RepoVisibility Detect(string projectPath)
    {
        var gitDir = Path.Combine(projectPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            return RepoVisibility.None;
        }

        if (!HasGitHubRemote(gitDir))
        {
            return RepoVisibility.None;
        }

        var visibility = Query(projectPath);
        return string.Equals(visibility, "PRIVATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(visibility, "INTERNAL", StringComparison.OrdinalIgnoreCase)
            ? RepoVisibility.Private
            : RepoVisibility.Public;
    }

    // Read rather than shelled out to: one file open beats a subprocess, and
    // the answer only has to be good enough to tell "on GitHub" from "not".
    private static bool HasGitHubRemote(string gitDir)
    {
        try
        {
            var config = Path.Combine(gitDir, "config");
            return File.Exists(config) &&
                   File.ReadAllText(config).Contains("github.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Query(string projectPath)
    {
        try
        {
            // Fully qualified: the Firepit.Core.Process namespace shadows the
            // System.Diagnostics type here.
            using var process = System.Diagnostics.Process.Start(new ProcessStartInfo("gh")
            {
                ArgumentList = { "repo", "view", "--json", "visibility", "-q", ".visibility" },
                WorkingDirectory = projectPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (Exception)
        {
            // gh missing or not authenticated — same answer as a failed call.
            return null;
        }
    }
}
