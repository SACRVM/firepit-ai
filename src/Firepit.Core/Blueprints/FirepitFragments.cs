using System.IO;

namespace Firepit.Core.Blueprints;

/// <summary>
/// Shared CLAUDE.md fragments living in the meta repo, imported by every
/// managed project rather than copied into it.
/// </summary>
/// <remarks>
/// <para>
/// They sit under <c>&lt;meta&gt;/projects/</c> — the namespace for what the
/// central repo holds about <i>other</i> projects — so they cannot collide with
/// the meta project's own <c>CLAUDE.md</c>, which addresses an agent working
/// inside that repo and nobody else.
/// </para>
/// <para>
/// The division of labour with the MCP <c>instructions</c> field matters, or
/// the two drift into two copies of the same text. Instructions carry product
/// behaviour — tool names, habits — and change with a release because they ship
/// inside the executable. Fragments carry policy, differ by repo class, and
/// change with a text editor. A fragment should point at the tools, not
/// re-explain them.
/// </para>
/// </remarks>
public static class FirepitFragments
{
    public const string DirName = "projects";
    public const string SharedFileName = "claude.md";
    public const string PublicFileName = "claude-github-public.md";
    public const string PrivateFileName = "claude-github-private.md";

    /// <summary>Marker for the CLAUDE.md section that carries the imports.</summary>
    public const string SectionMarker = "claude-firepit-fragments";

    /// <summary>Replaced per project when the blueprint section is applied.</summary>
    public const string SharedToken = "{{FIREPIT_FRAGMENT}}";
    public const string ClassToken = "{{FIREPIT_CLASS_FRAGMENT}}";

    public static string DirectoryFor(string metaProjectPath) =>
        Path.Combine(Path.GetFullPath(metaProjectPath), DirName);

    public static string SharedPath(string metaProjectPath) =>
        Path.Combine(DirectoryFor(metaProjectPath), SharedFileName);

    public static string ClassPath(string metaProjectPath, bool isPublic) =>
        Path.Combine(DirectoryFor(metaProjectPath), isPublic ? PublicFileName : PrivateFileName);

    /// <summary>
    /// How a project's CLAUDE.md should refer to a fragment. Relative where
    /// that works — a relative import survives the whole tree being moved —
    /// and absolute where it cannot, which is any project outside the projects
    /// root, such as one on a network share.
    /// </summary>
    public static string ImportPath(string projectPath, string fragmentPath)
    {
        var from = Path.GetFullPath(projectPath);
        var to = Path.GetFullPath(fragmentPath);

        if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            return to;
        }

        var relative = Path.GetRelativePath(from, to).Replace('\\', '/');

        // A path that climbs more than one level is a sign the project does not
        // sit next to the meta repo at all; absolute is then both shorter and
        // less likely to be wrong.
        return relative.StartsWith("../../", StringComparison.Ordinal) ? to : relative;
    }

    /// <summary>
    /// Replaces the import tokens in a CLAUDE.md section with paths that work
    /// from <paramref name="projectPath"/>. Content without tokens comes back
    /// untouched, so this is safe to run over every section.
    /// </summary>
    public static string ResolveSection(
        string content, string projectPath, string metaProjectPath)
    {
        if (!content.Contains(SharedToken, StringComparison.Ordinal) &&
            !content.Contains(ClassToken, StringComparison.Ordinal))
        {
            return content;
        }

        var isPublic = Projects.GitHubVisibility.IsPublic(projectPath);
        return content
            .Replace(
                SharedToken,
                ImportPath(projectPath, SharedPath(metaProjectPath)),
                StringComparison.Ordinal)
            .Replace(
                ClassToken,
                ImportPath(projectPath, ClassPath(metaProjectPath, isPublic)),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the three fragments if they are missing. Never overwrites: they
    /// exist to be edited, and an edit that gets reset on the next launch is
    /// worse than no fragment at all.
    /// </summary>
    public static IReadOnlyList<string> EnsureSeeded(string metaProjectPath)
    {
        var dir = DirectoryFor(metaProjectPath);
        Directory.CreateDirectory(dir);

        var created = new List<string>();
        foreach (var (name, content) in new[]
        {
            (SharedFileName, SharedFragment),
            (PublicFileName, PublicFragment),
            (PrivateFileName, PrivateFragment),
        })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, content);
            created.Add(name);
        }

        return created;
    }

    public const string SharedFragment =
        """
        # Firepit

        This project is hosted in Firepit, a shell that runs one agent session per
        project. Firepit is a transparent host: it never reads or interprets what
        the terminal prints. Anything the user should see or keep has to be pinned,
        sent, or saved on purpose.

        The tools and the habits that go with them arrive with the MCP handshake —
        `firepit_artifact_add`, `firepit_inbox_list`, `firepit_knowledge_search` and
        the rest. Do not look for them documented here; this file carries policy,
        the handshake carries behaviour, and keeping them apart is what stops the
        two from drifting.

        ## Where things live

        - `.firepit/` in a project belongs to that project: its inbox, its runs, its
          knowledge, its config. Nothing about *other* projects goes in it.
        - `.firepit/knowledge` is either a directory holding the docs, or a file
          holding the path to the directory that does. If it is a file, the docs are
          deliberately not in this repo — do not try to commit them here.
        - The central repo holds what spans projects: the shared knowledge base, the
          per-project stores for repos that cannot keep their own, and this file.

        ## More

        Ask for `firepit_knowledge_search` with scope `both` before researching
        anything that smells like it has been decided before. The global base is
        where cross-project decisions end up.
        """;

    public const string PublicFragment =
        """
        # This repo is public

        Anything committed here is readable by anyone, permanently, including in the
        history after a later deletion. That changes what may be written down.

        - Research notes, investigation logs and anything naming private
          infrastructure belong in the knowledge base, not in files in this repo.
          If `.firepit/knowledge` is a pointer file, that is already arranged — save
          with `firepit_knowledge_add` and it lands outside this repo.
        - No credentials, tokens, internal hostnames, or paths that reveal the
          layout of the author's machine.
        - Commit messages are public too. Describe the change, not the context that
          produced it.
        - When unsure whether something belongs in the repo or in knowledge, put it
          in knowledge and mention it. The reverse is not reversible.
        """;

    public const string PrivateFragment =
        """
        # This repo is private

        Only the author reaches it, so notes, research and working documents can be
        committed here directly. `.firepit/knowledge/` is a normal directory and its
        contents are part of the repo.

        This is the permissive case, not the careless one: a private repo can still
        become public, and secrets in history survive that change. Keep credentials
        in the secret store rather than in files, exactly as you would elsewhere.
        """;
}
