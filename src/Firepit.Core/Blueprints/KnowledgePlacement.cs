using System.IO;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Projects;

namespace Firepit.Core.Blueprints;

/// <summary>
/// Whether a project's knowledge documents are in a defensible place.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the scope integrity check, which asks whether the index still
/// matches the documents. That question can be answered "sound" while the
/// documents sit in a repository anyone can read — which is exactly what
/// happened: twelve public repos held a knowledge base, one of them with eleven
/// documents pushed to a public remote, and every one of them passed the check.
/// </para>
/// <para>
/// Two rules, both about the same thing — research must not end up in a public
/// repository:
/// </para>
/// <list type="number">
///   <item>A public repo's documents live behind a pointer file, not in it.</item>
///   <item>A redirected repo does not version its generated
///   <c>knowledge-pinned.md</c>: it is compiled from documents deliberately kept
///   outside, so committing it carries their text back in.</item>
/// </list>
/// <para>
/// The rules are checked here rather than trusted to whoever applied them once,
/// because a repository's visibility is not a constant — a repo flipped from
/// private to public takes its knowledge base with it, and nothing else would
/// notice.
/// </para>
/// </remarks>
public static class KnowledgePlacement
{
    /// <param name="IsTracked">
    /// Asks git whether a repo-relative path is versioned. Injected so this
    /// stays testable without a repository.
    /// </param>
    public sealed record Finding(string Severity, string Message, string? Fix = null);

    public static IReadOnlyList<Finding> Check(
        string projectPath,
        string projectName,
        string metaProjectPath,
        Func<string, string, bool>? isTracked = null,
        Func<string, VisibilityResult>? visibility = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var findings = new List<Finding>();
        isTracked ??= GitTracks;
        visibility ??= GitHubVisibility.Inspect;

        var redirected = KnowledgeRedirect.IsRedirected(projectPath);
        var localDir = Path.Combine(projectPath, ".firepit", "knowledge");
        var hosted = Path.Combine(metaProjectPath, "projects", projectName, "knowledge");

        // Rule 1 — a public repo keeps no knowledge base inside itself.
        //
        // `Certain` is required here and deliberately not required where the
        // rule is *applied*. Acting on the fail-safe guess is cheap and
        // reversible; reporting a confident error on it is the inversion that
        // told every private repo it was misconfigured whenever `gh` was down.
        var seen = visibility(projectPath);
        if (seen.Value == RepoVisibility.Public && seen.Certain && !redirected)
        {
            var documents = Directory.Exists(localDir)
                ? Directory.EnumerateFiles(localDir, "*.md", SearchOption.AllDirectories)
                    .Count(f => !string.Equals(
                        Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
                : 0;

            findings.Add(documents > 0
                ? new Finding(
                    "error",
                    $"This repository is public and {documents} knowledge document(s) are " +
                    $"committed inside it ({localDir}). Anything here is readable by anyone, " +
                    "permanently, including after a later deletion.",
                    $"open Project settings and host them at {hosted}, then commit the removal")
                : new Finding(
                    "warning",
                    "This repository is public but keeps its knowledge base inside itself. " +
                    "It is empty, so nothing is exposed yet — the next document saved here would be.",
                    $"firepit_blueprint_apply(projectName: \"{projectName}\") now hosts it at {hosted}"));
        }

        // Rule 2 — a redirected repo does not version the generated digest.
        //
        // Severity follows the stakes, not the rule. In a public repo this
        // carries private text into a place anyone can read; in a private one
        // it is derived data churning in git, which is untidy and not a leak.
        // Grading both as errors would dilute the ones that matter.
        if (redirected && isTracked(projectPath, ".firepit/knowledge-pinned.md"))
        {
            var isPublic = seen.Value == RepoVisibility.Public && seen.Certain;
            findings.Add(new Finding(
                isPublic ? "error" : "warning",
                isPublic
                    ? "knowledge-pinned.md is versioned in this public repo, but the documents " +
                      "it is compiled from live outside it on purpose. Committing the digest " +
                      "carries their text back in, where anyone can read it."
                    : "knowledge-pinned.md is versioned here, but it is generated from " +
                      "documents that live outside this repo — so it changes whenever they do, " +
                      "for a file nothing here authors.",
                "git rm --cached .firepit/knowledge-pinned.md — the file stays on disk for " +
                "CLAUDE.md to import"));
        }

        return findings;
    }

    private static bool GitTracks(string projectPath, string relativePath)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(projectPath, ".git")))
            {
                return false;
            }

            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("git")
                {
                    ArgumentList = { "-C", projectPath, "ls-files", "--error-unmatch", relativePath },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(5_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            // No git, no answer. Reporting "tracked" on a guess would be the
            // same mistake the visibility check used to make.
            return false;
        }
    }
}
