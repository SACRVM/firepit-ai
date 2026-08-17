using System.IO;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// Whether a project's knowledge lives somewhere other than its own repo.
/// </summary>
/// <remarks>
/// <para>
/// The full resolution — reading the pointer, validating the target, reporting
/// why a bad one is bad — is <c>Firepit.Knowledge.KnowledgeLocator</c>. This is
/// the one fact the scaffolding and blueprint layers need, and they cannot ask
/// the knowledge assembly because it is standalone.
/// </para>
/// <para>
/// They need it because <c>.firepit/knowledge</c> being a file rather than a
/// directory changes what conformance means. Blueprint files that live inside
/// that directory — the conventions README — are not missing from a redirected
/// project, they are inapplicable to it. Reporting them as missing sends an
/// agent to run an apply that replaces the pointer with a directory, silently
/// undoing the redirect: a public repo starts committing its research again,
/// which is the exact outcome the pointer existed to prevent.
/// </para>
/// </remarks>
public static class KnowledgeRedirect
{
    public const string KnowledgePath = ".firepit/knowledge";

    public static bool IsRedirected(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var path = Path.Combine(projectPath, ".firepit", "knowledge");
        return File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>
    /// True for a blueprint file that would have to live inside the project's
    /// own knowledge directory.
    /// </summary>
    public static bool IsInsideKnowledgeDir(string relativePath) =>
        relativePath.Replace('\\', '/')
            .StartsWith(KnowledgePath + "/", StringComparison.OrdinalIgnoreCase);
}
