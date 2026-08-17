namespace Firepit.Knowledge;

/// <summary>
/// The three paths one knowledge scope owns. Split out because they no longer
/// all derive from the project path: a pointer file can send the docs
/// elsewhere, while the pinned digest must stay in the project — CLAUDE.md
/// imports it by a path relative to the project root.
/// </summary>
/// <param name="DocsDir">Directory holding the scope's <c>*.md</c> — the committed truth.</param>
/// <param name="IndexPath">The derived SQLite index. Never committed.</param>
/// <param name="DigestPath">Generated <c>knowledge-pinned.md</c>, always inside the project.</param>
public sealed record KnowledgeStoreLocation(string DocsDir, string IndexPath, string DigestPath)
{
    /// <summary>
    /// Resolves the layout for a project, following a pointer file if there is
    /// one. See <see cref="KnowledgeLocator"/>.
    /// </summary>
    public static KnowledgeStoreLocation For(string projectPath) =>
        For(projectPath, KnowledgeLocator.Resolve(projectPath).DocsDir);

    /// <summary>
    /// Layout for the shared base at the root of the meta repo.
    /// </summary>
    /// <remarks>
    /// Everything here is a sibling of the documents directory, digest
    /// included — and that last part is the whole point. The global base is not
    /// any one project's knowledge, so deriving its digest from the meta
    /// project's path (as <see cref="For(string, string)"/> does) gave it the
    /// same <c>knowledge-pinned.md</c> as the meta project's own local scope.
    /// Two scopes, one file, different documents: each pass overwrote the
    /// other's, forever, and an integrity check reported "regenerated" on every
    /// run without ever converging.
    /// <para>
    /// In the pre-split layout the two are the same directory anyway, so this
    /// resolves to the historical path and nothing moves.
    /// </para>
    /// </remarks>
    public static KnowledgeStoreLocation ForGlobal(string metaProjectPath)
    {
        var full = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(KnowledgeLayout.ResolveGlobalDocsDir(metaProjectPath)));
        return new KnowledgeStoreLocation(full, full + ".db", full + "-pinned.md");
    }

    /// <summary>Layout for an already-resolved docs directory.</summary>
    public static KnowledgeStoreLocation For(string projectPath, string docsDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(docsDir));
        return new KnowledgeStoreLocation(
            full,
            // Sibling of the docs directory, same name plus .db. For a project
            // keeping its own docs that is the historical
            // `.firepit/knowledge.db`; for a redirected one it lands next to
            // the target, so repos sharing a directory share one index too.
            full + ".db",
            Path.Combine(
                Path.GetFullPath(projectPath), ".firepit", Indexing.PinnedDigest.FileName));
    }
}
