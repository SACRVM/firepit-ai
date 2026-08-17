using System.IO;
using Firepit.Core.Projects;

namespace Firepit.Core.Blueprints;

/// <param name="Severity">
/// <c>"error"</c> for something that misleads an agent about what it may
/// commit; <c>"warning"</c> for drift that only costs conventions.
/// </param>
public sealed record FragmentFinding(string Severity, string Message, string? Fix = null);

/// <summary>
/// Checks that a project's shared-fragment imports still say what they should.
/// </summary>
/// <remarks>
/// Blueprint conformance is a marker check — the section is there or it is not.
/// That is the right test for text that never changes, and the wrong one here:
/// the class fragment encodes whether the repository is public, and a
/// repository's visibility is not a constant. A repo flipped from private to
/// public keeps importing the private policy, which tells its agent that
/// research may be committed — into a repository anyone can now read.
/// </remarks>
public static class FragmentIntegrity
{
    public static IReadOnlyList<FragmentFinding> Check(string projectPath, string metaProjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(metaProjectPath);

        var findings = new List<FragmentFinding>();
        var claudeMdPath = Path.Combine(projectPath, "CLAUDE.md");
        if (!File.Exists(claudeMdPath))
        {
            return findings;
        }

        string claudeMd;
        try
        {
            claudeMd = File.ReadAllText(claudeMdPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new FragmentFinding("warning", $"CLAUDE.md could not be read: {ex.Message}")];
        }

        if (!claudeMd.Contains(FirepitFragments.SectionMarker, StringComparison.Ordinal))
        {
            findings.Add(new FragmentFinding(
                "warning",
                "CLAUDE.md does not import the shared Firepit fragments.",
                "run firepit_blueprint_apply for this project"));
            return findings;
        }

        // Every import in the section has to resolve from here. A moved tree or
        // a renamed central repo leaves a line that silently loads nothing.
        foreach (var import in ImportsIn(claudeMd))
        {
            var resolved = Path.GetFullPath(Path.Combine(projectPath, import));
            if (!File.Exists(resolved))
            {
                findings.Add(new FragmentFinding(
                    "warning",
                    $"CLAUDE.md imports '{import}', which does not exist ({resolved}).",
                    "re-run firepit_blueprint_apply, or restore the file in the central repo"));
            }
        }

        var visibility = GitHubVisibility.Detect(projectPath);
        var expected = visibility switch
        {
            RepoVisibility.Public => FirepitFragments.PublicFileName,
            RepoVisibility.Private => FirepitFragments.PrivateFileName,
            _ => null,
        };

        var importsPublic = claudeMd.Contains(FirepitFragments.PublicFileName, StringComparison.OrdinalIgnoreCase);
        var importsPrivate = claudeMd.Contains(FirepitFragments.PrivateFileName, StringComparison.OrdinalIgnoreCase);

        if (expected is null && (importsPublic || importsPrivate))
        {
            findings.Add(new FragmentFinding(
                "warning",
                "This project is not on GitHub, but its CLAUDE.md imports a public/private policy fragment.",
                "remove that import line"));
        }
        else if (visibility == RepoVisibility.Public && importsPrivate)
        {
            // The one finding here that is a safety problem rather than drift.
            findings.Add(new FragmentFinding(
                "error",
                "This repository is PUBLIC but its CLAUDE.md imports the private policy fragment, " +
                "which tells agents that research may be committed here.",
                $"change the import to {FirepitFragments.PublicFileName}"));
        }
        else if (visibility == RepoVisibility.Private && importsPublic)
        {
            findings.Add(new FragmentFinding(
                "warning",
                "This repository is private but imports the public policy fragment — stricter than it needs to be.",
                $"change the import to {FirepitFragments.PrivateFileName}"));
        }

        foreach (var (name, path) in new[]
        {
            (FirepitFragments.SharedFileName, FirepitFragments.SharedPath(metaProjectPath)),
            (FirepitFragments.PublicFileName, FirepitFragments.ClassPath(metaProjectPath, true)),
            (FirepitFragments.PrivateFileName, FirepitFragments.ClassPath(metaProjectPath, false)),
        })
        {
            if (!File.Exists(path))
            {
                findings.Add(new FragmentFinding(
                    "warning",
                    $"The shared fragment {name} is missing from the central repo.",
                    "run firepit_blueprint_apply for any project to seed it again"));
            }
        }

        return findings;
    }

    private static IEnumerable<string> ImportsIn(string claudeMd)
    {
        foreach (var raw in claudeMd.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('@') && line.Contains(FirepitFragments.DirName + "/", StringComparison.OrdinalIgnoreCase))
            {
                yield return line[1..].Trim();
            }
        }
    }
}
