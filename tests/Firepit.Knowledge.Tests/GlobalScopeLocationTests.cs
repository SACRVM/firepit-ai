namespace Firepit.Knowledge.Tests;

/// <summary>
/// The shared base and the meta project's own knowledge are two scopes in one
/// repository. Everything each owns has to be its own — the index was, the
/// pinned digest was not.
/// </summary>
public sealed class GlobalScopeLocationTests : IDisposable
{
    private readonly string _root;
    private readonly string _meta;

    public GlobalScopeLocationTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-global-location", Guid.NewGuid().ToString("N"));
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(_meta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheGlobalDigest_IsNotTheMetaProjectsOwnDigest()
    {
        // Both scopes have the meta repo as their project path, so deriving the
        // digest from that path gave them one file. They hold different
        // documents, so each index pass overwrote the other's digest: the
        // meta project's CLAUDE.md import became a coin flip, and an integrity
        // check reported "regenerated knowledge-pinned.md" on every run without
        // ever converging.
        Directory.CreateDirectory(KnowledgeLayout.GlobalDocsDir(_meta));
        Directory.CreateDirectory(KnowledgeLayout.LocalDocsDir(_meta));

        var global = KnowledgeStoreLocation.ForGlobal(_meta);
        var local = KnowledgeStoreLocation.For(_meta);

        Assert.NotEqual(global.DocsDir, local.DocsDir);
        Assert.NotEqual(global.DigestPath, local.DigestPath);
        Assert.NotEqual(global.IndexPath, local.IndexPath);
    }

    [Fact]
    public void EverythingTheGlobalScopeOwns_SitsBesideItsDocuments()
    {
        Directory.CreateDirectory(KnowledgeLayout.GlobalDocsDir(_meta));

        var global = KnowledgeStoreLocation.ForGlobal(_meta);

        Assert.Equal(Path.Combine(_meta, "knowledge"), global.DocsDir);
        Assert.Equal(Path.Combine(_meta, "knowledge.db"), global.IndexPath);
        Assert.Equal(Path.Combine(_meta, "knowledge-pinned.md"), global.DigestPath);
    }

    [Fact]
    public void InThePreSplitLayout_NothingMoves()
    {
        // No <meta>/knowledge yet: the global base is still the meta project's
        // own .firepit/knowledge, it is the only scope there, and its digest is
        // the historical path the meta CLAUDE.md already imports.
        Directory.CreateDirectory(KnowledgeLayout.LocalDocsDir(_meta));

        var global = KnowledgeStoreLocation.ForGlobal(_meta);

        Assert.Equal(Path.Combine(_meta, ".firepit", "knowledge"), global.DocsDir);
        Assert.Equal(
            Path.Combine(_meta, ".firepit", Indexing.PinnedDigest.FileName), global.DigestPath);
    }
}
