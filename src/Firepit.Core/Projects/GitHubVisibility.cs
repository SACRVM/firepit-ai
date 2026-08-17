using System.Diagnostics;
using System.IO;

namespace Firepit.Core.Projects;

/// <summary>
/// Whether a project's GitHub repository is public. Asked once, when a
/// blueprint is applied, to pick which policy fragment its CLAUDE.md imports.
/// </summary>
public static class GitHubVisibility
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// True when the repo is known to be private. Everything else — no git, no
    /// GitHub remote, no <c>gh</c> on PATH, a call that fails or times out —
    /// answers false.
    /// </summary>
    /// <remarks>
    /// The default is deliberately the strict one. Treating a private repo as
    /// public costs an unnecessarily careful rule; treating a public repo as
    /// private invites research into a repository the whole world can read.
    /// </remarks>
    public static bool IsPublic(string projectPath)
    {
        if (!Directory.Exists(Path.Combine(projectPath, ".git")))
        {
            return true;
        }

        var visibility = Query(projectPath);
        return !string.Equals(visibility, "PRIVATE", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(visibility, "INTERNAL", StringComparison.OrdinalIgnoreCase);
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
            // gh missing, not authenticated, no remote — all the same answer.
            return null;
        }
    }
}
