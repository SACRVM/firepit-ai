using System.IO;

namespace Firepit.Core.Projects;

/// <summary>
/// Adds paths to a repository's <c>.git/info/exclude</c> — git's local, never
/// committed ignore list.
/// </summary>
/// <remarks>
/// Deliberately not <c>.gitignore</c>. A gitignore entry is itself committed,
/// so it announces to everyone reading a public repo that a private file
/// exists there and what it is called. <c>info/exclude</c> is per-clone and
/// never shared, which is the whole point when the file being hidden is the
/// compiled digest of private research.
/// </remarks>
public static class GitLocalExclude
{
    private const string Header = "# Added by Firepit — knowledge stored outside this repo.";

    /// <summary>
    /// Ensures <paramref name="relativePath"/> is excluded locally. No-op when
    /// the project is not a git repo (nothing to hide it from) or the entry is
    /// already there. Returns true when the file was written.
    /// </summary>
    public static bool Ensure(string projectPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var gitDir = Path.Combine(projectPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            return false;
        }

        var infoDir = Path.Combine(gitDir, "info");
        var excludePath = Path.Combine(infoDir, "exclude");
        var entry = relativePath.Replace('\\', '/');

        var lines = File.Exists(excludePath)
            ? File.ReadAllLines(excludePath).ToList()
            : [];

        if (lines.Any(l => string.Equals(l.Trim(), entry, StringComparison.Ordinal)))
        {
            return false;
        }

        Directory.CreateDirectory(infoDir);
        if (lines.Count > 0 && lines[^1].Trim().Length > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add(Header);
        lines.Add(entry);
        File.WriteAllLines(excludePath, lines);
        return true;
    }
}
