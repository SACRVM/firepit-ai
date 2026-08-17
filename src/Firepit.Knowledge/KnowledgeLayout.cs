namespace Firepit.Knowledge;

/// <summary>
/// The three places knowledge lives, and the one rule that separates them.
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;repo&gt;/.firepit/</c> belongs to that repo and nothing else. It holds the
/// repo's own inbox, runs and knowledge. That is true of the meta project too
/// — it is a project like any other, with its own local knowledge.
/// </para>
/// <para>
/// Which is why what the meta project holds <i>about other projects</i> cannot
/// live there: it would break the rule. It goes under
/// <c>&lt;meta&gt;/projects/&lt;name&gt;/</c> instead, and the base every project
/// reads sits at <c>&lt;meta&gt;/knowledge/</c> — the knowledge of the whole tree
/// rather than of one repo in it.
/// </para>
/// <para>
/// Before this split the global base and the meta project's own knowledge were
/// the same directory, so anything saved globally was also the administration
/// project's local notes, and the other way round.
/// </para>
/// </remarks>
public static class KnowledgeLayout
{
    public const string DirName = "knowledge";
    public const string HostedRootName = "projects";

    /// <summary>A repo's own knowledge. The uniform rule, meta project included.</summary>
    public static string LocalDocsDir(string projectPath) =>
        Path.Combine(Path.GetFullPath(projectPath), ".firepit", DirName);

    /// <summary>The base every project reads, at the root of the meta repo.</summary>
    public static string GlobalDocsDir(string metaProjectPath) =>
        Path.Combine(Path.GetFullPath(metaProjectPath), DirName);

    /// <summary>
    /// Where the meta repo hosts a project that cannot keep its own docs —
    /// a public repo, or several repos sharing one base.
    /// </summary>
    public static string HostedDocsDir(string metaProjectPath, string projectName) =>
        Path.Combine(Path.GetFullPath(metaProjectPath), HostedRootName, projectName, DirName);

    /// <summary>
    /// True while the global base still sits in the meta project's own
    /// <c>.firepit/knowledge/</c> — the pre-split layout, where one directory
    /// served both roles.
    /// </summary>
    /// <remarks>
    /// Firepit keeps reading it rather than starting an empty global base and
    /// quietly demoting the existing docs to one project's local notes. While
    /// this is true the meta project gets no separate local scope, because the
    /// directory that would back it is the global base.
    /// </remarks>
    public static bool UsesLegacyGlobal(string metaProjectPath) =>
        !Directory.Exists(GlobalDocsDir(metaProjectPath)) &&
        Directory.Exists(LocalDocsDir(metaProjectPath));

    /// <summary>Resolves the global base, new layout preferred.</summary>
    public static string ResolveGlobalDocsDir(string metaProjectPath) =>
        UsesLegacyGlobal(metaProjectPath)
            ? LocalDocsDir(metaProjectPath)
            : GlobalDocsDir(metaProjectPath);
}
