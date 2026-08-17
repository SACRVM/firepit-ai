namespace Firepit.Knowledge;

/// <summary>
/// The three paths one knowledge scope owns. Split out because they no longer
/// all derive from the project path: a project may keep its docs in another
/// project's store, while the pinned digest must stay in the project itself —
/// CLAUDE.md imports it from there.
/// </summary>
/// <param name="DocsDir">Directory holding the scope's <c>*.md</c> — the committed truth.</param>
/// <param name="IndexPath">The derived SQLite index. Never committed.</param>
/// <param name="DigestPath">
/// Generated <c>knowledge-pinned.md</c>. Always inside the project it belongs
/// to, whatever store the docs live in, because the <c>@</c> import in that
/// project's CLAUDE.md resolves relative to the project root.
/// </param>
public sealed record KnowledgeStoreLocation(string DocsDir, string IndexPath, string DigestPath)
{
    /// <summary>The default: everything under the project's own <c>.firepit/</c>.</summary>
    public static KnowledgeStoreLocation BesideProject(string projectPath)
    {
        var dir = Path.Combine(Path.GetFullPath(projectPath), ".firepit");
        return new KnowledgeStoreLocation(
            Path.Combine(dir, "knowledge"),
            Path.Combine(dir, Store.KnowledgeStore.IndexFileName),
            Path.Combine(dir, Indexing.PinnedDigest.FileName));
    }

    /// <summary>
    /// Docs live in another project's store, under a folder named after the
    /// project they belong to: <c>{store}/knowledge/{folder}/*.md</c>. This is
    /// what keeps research out of a public repo — the store project is private,
    /// so nothing is committed where it should not be.
    /// </summary>
    public static KnowledgeStoreLocation InStore(
        string storeProjectPath, string folder, string projectPath)
    {
        var root = Path.Combine(Path.GetFullPath(storeProjectPath), "knowledge");
        return new KnowledgeStoreLocation(
            // The index sits *next to* the docs folder rather than inside it,
            // mirroring the beside-project layout and keeping the docs folder
            // pure markdown. One `knowledge/*.db` line gitignores every index.
            Path.Combine(root, folder),
            Path.Combine(root, folder + ".db"),
            Path.Combine(
                Path.GetFullPath(projectPath), ".firepit", Indexing.PinnedDigest.FileName));
    }
}
