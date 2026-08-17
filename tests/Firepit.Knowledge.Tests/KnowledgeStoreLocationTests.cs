namespace Firepit.Knowledge.Tests;

/// <summary>
/// A project may keep its knowledge in another project's store so a public
/// repo never commits private research. What must survive that redirect: the
/// docs move, the index follows them, and the pinned digest stays behind —
/// CLAUDE.md imports it by a path relative to the project root.
/// </summary>
public sealed class KnowledgeStoreLocationTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _store;
    private readonly KnowledgeService _service;

    public KnowledgeStoreLocationTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-knowledge-store-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "public-repo");
        _store = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
        Directory.CreateDirectory(_store);

        _service = new KnowledgeService(modelDataRoot: Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void BesideProject_IsTheLayoutThatShippedBefore()
    {
        var location = KnowledgeStoreLocation.BesideProject(_project);

        Assert.Equal(Path.Combine(_project, ".firepit", "knowledge"), location.DocsDir);
        Assert.Equal(Path.Combine(_project, ".firepit", "knowledge.db"), location.IndexPath);
        Assert.Equal(
            Path.Combine(_project, ".firepit", "knowledge-pinned.md"), location.DigestPath);
    }

    [Fact]
    public void InStore_MovesDocsAndIndexButLeavesTheDigestInTheProject()
    {
        var location = KnowledgeStoreLocation.InStore(_store, "public-repo", _project);

        Assert.Equal(Path.Combine(_store, "knowledge", "public-repo"), location.DocsDir);
        // Next to the folder, not inside it — the docs folder stays pure
        // markdown and one `knowledge/*.db` line covers every index.
        Assert.Equal(Path.Combine(_store, "knowledge", "public-repo.db"), location.IndexPath);
        Assert.Equal(
            Path.Combine(_project, ".firepit", "knowledge-pinned.md"), location.DigestPath);
    }

    [Fact]
    public async Task ARedirectedScope_WritesNothingIntoTheProjectItself()
    {
        _service.SyncScopes(
        [
            new KnowledgeScopeRegistration(
                "public-repo", _project, KnowledgeStoreLocation.InStore(_store, "public-repo", _project)),
        ]);

        await _service.AddDocumentAsync("public-repo", "Private Finding", "Body text.", pinned: false);

        var inStore = Directory.GetFiles(
            Path.Combine(_store, "knowledge", "public-repo"), "*.md", SearchOption.AllDirectories);
        Assert.Single(inStore);

        // The whole point: nothing landed under the project's own knowledge dir.
        Assert.False(Directory.Exists(Path.Combine(_project, ".firepit", "knowledge")));
    }

    [Fact]
    public async Task ARedirectedScope_StillWritesTheDigestIntoTheProject()
    {
        _service.SyncScopes(
        [
            new KnowledgeScopeRegistration(
                "public-repo", _project, KnowledgeStoreLocation.InStore(_store, "public-repo", _project)),
        ]);

        await _service.AddDocumentAsync("public-repo", "Reflex Rule", "Always do this.", pinned: true);

        // CLAUDE.md's `@.firepit/knowledge-pinned.md` resolves against the
        // project root, so a digest in the store would silently stop loading.
        var digest = Path.Combine(_project, ".firepit", "knowledge-pinned.md");
        Assert.True(File.Exists(digest), $"expected a digest at {digest}");
        Assert.Contains("Always do this.", await File.ReadAllTextAsync(digest));
    }
}
