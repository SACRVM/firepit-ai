using System.IO;
using Firepit.Core.Blueprints;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Platform;

/// <summary>
/// Seeds the <c>.firepit</c> central project at the projects root. firepitd
/// calls it on start when the central project is missing. Idempotent per
/// file: pre-existing files are never overwritten (the user may have curated
/// them). Returns the list of files actually written.
/// </summary>
public sealed class MetaProjectBootstrapper
{
    public const string MetaProjectName = ".firepit";

    public string GetMetaProjectPath(string projectsRoot) =>
        Path.Combine(projectsRoot, MetaProjectName);

    /// <summary>
    /// Whether the central project is there in the sense project discovery
    /// uses: a <c>CLAUDE.md</c> or a <c>.claude/</c> folder. A bare
    /// <c>.firepit</c> holding only a search index is not a project yet.
    /// </summary>
    public bool Exists(string projectsRoot)
    {
        var root = GetMetaProjectPath(projectsRoot);
        return File.Exists(Path.Combine(root, "CLAUDE.md")) ||
               Directory.Exists(Path.Combine(root, ".claude"));
    }

    public IReadOnlyList<string> Bootstrap(string projectsRoot)
    {
        ArgumentNullException.ThrowIfNull(projectsRoot);
        var root = Path.GetFullPath(GetMetaProjectPath(projectsRoot));
        Directory.CreateDirectory(root);

        var written = new List<string>();
        WriteIfMissing(written, Path.Combine(root, "CLAUDE.md"),  MetaProjectTemplates.ClaudeMd(root));
        WriteIfMissing(written, Path.Combine(root, "README.md"),  MetaProjectTemplates.ReadmeMd);
        WriteIfMissing(written, Path.Combine(root, ".mcp.json"),  MetaProjectTemplates.McpJson);
        WriteIfMissing(written, Path.Combine(root, ".gitignore"), MetaProjectTemplates.GitIgnore);
        WriteIfMissing(written, ProjectConfigScaffold.GetConfigPath(root), MetaProjectTemplates.ConfigJson);

        // A central project on the pre-split layout keeps its global base in
        // .firepit/knowledge/. A knowledge/ seeded beside it would quietly
        // demote those documents to local notes (KnowledgeLayout.UsesLegacyGlobal),
        // so such a project gets neither README.
        var globalDocs = Path.Combine(root, "knowledge");
        var localDocs = Path.Combine(root, ".firepit", "knowledge");
        if (Directory.Exists(globalDocs) || !Directory.Exists(localDocs))
        {
            WriteIfMissing(written, Path.Combine(globalDocs, "README.md"), MetaProjectTemplates.GlobalKnowledgeReadme);
            WriteIfMissing(written, Path.Combine(root, "knowledge-pinned.md"), FirepitBlueprintDefaults.PinnedDigestSeed);
            if (ProjectScaffolding.EnsureKnowledgeReadme(root))
            {
                written.Add(Path.Combine(localDocs, "README.md"));
            }
        }

        if (ProjectScaffolding.EnsurePinnedDigestSeed(root))
        {
            written.Add(Path.Combine(root, ".firepit", "knowledge-pinned.md"));
        }

        written.AddRange(FirepitFragments.EnsureSeeded(root)
            .Select(name => Path.Combine(FirepitFragments.DirectoryFor(root), name)));

        CleanupDeadRootInbox(root);
        return written;
    }

    // v0.5.0 and earlier created a root-level `inbox/` that no code ever
    // wrote to — actual inbox traffic goes to `.firepit/inbox/`. Remove the
    // leftover empty directory on existing meta-projects. Only touches
    // directories that are empty except for the bootstrapped .gitkeep, so
    // user-curated content is safe.
    private static void CleanupDeadRootInbox(string root)
    {
        var deadInbox = Path.Combine(root, "inbox");
        if (!Directory.Exists(deadInbox)) return;

        var entries = Directory.GetFileSystemEntries(deadInbox);
        if (entries.Length == 0)
        {
            Directory.Delete(deadInbox);
            return;
        }

        if (entries.Length == 1 &&
            string.Equals(Path.GetFileName(entries[0]), ".gitkeep", StringComparison.OrdinalIgnoreCase) &&
            new FileInfo(entries[0]).Length == 0)
        {
            File.Delete(entries[0]);
            Directory.Delete(deadInbox);
        }
    }

    private static void WriteIfMissing(List<string> written, string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        written.Add(path);
    }
}
