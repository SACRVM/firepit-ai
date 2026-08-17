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

/// <param name="Value">The visibility to act on — never "unknown".</param>
/// <param name="Certain">
/// False when <paramref name="Value"/> is the fail-safe guess rather than an
/// answer. Choosing a fragment may use the guess; auditing one may not.
/// </param>
public sealed record VisibilityResult(RepoVisibility Value, bool Certain);

public static class GitHubVisibility
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    // gh missing is a property of the machine, not of the repository. Without
    // this latch an offline integrity check pays the full timeout once per
    // project — minutes of blocking for an answer that was never coming.
    private static bool _ghUnavailable;

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
    public static RepoVisibility Detect(string projectPath) => Inspect(projectPath).Value;

    /// <summary>
    /// <see cref="Detect"/>, plus whether the answer was read or guessed.
    /// </summary>
    /// <remarks>
    /// The fail-safe default is right for <i>choosing</i> a fragment and wrong
    /// for <i>auditing</i> one: an offline machine, or one without <c>gh</c>,
    /// would otherwise have every private repo reported as "PUBLIC but importing
    /// the private policy" — turning "we could not tell" into a confident wrong
    /// error, which is the exact inversion this subsystem exists to prevent.
    /// </remarks>
    public static VisibilityResult Inspect(string projectPath)
    {
        var gitDir = Path.Combine(projectPath, ".git");
        if (!Directory.Exists(gitDir) || !HasGitHubRemote(gitDir))
        {
            // Not a guess: no .git, or no GitHub remote, is an answer.
            return new VisibilityResult(RepoVisibility.None, Certain: true);
        }

        var visibility = Query(projectPath);
        if (visibility is null)
        {
            return new VisibilityResult(RepoVisibility.Public, Certain: false);
        }

        var isPrivate =
            string.Equals(visibility, "PRIVATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(visibility, "INTERNAL", StringComparison.OrdinalIgnoreCase);
        return new VisibilityResult(
            isPrivate ? RepoVisibility.Private : RepoVisibility.Public, Certain: true);
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
        if (_ghUnavailable)
        {
            return null;
        }

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

            if (process.ExitCode != 0)
            {
                return null;
            }

            var value = stdout.Trim();
            return value.Length == 0 ? null : value;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // gh is not on PATH. True for every project on this machine, so
            // stop asking — the alternative is one process launch and one
            // timeout per project.
            _ghUnavailable = true;
            return null;
        }
        catch (Exception)
        {
            // Not authenticated, no network, repo gone: per-repository, so no
            // latch — another project may still answer.
            return null;
        }
    }
}
