namespace Firepit.Knowledge.Tests;

/// <summary>
/// `.firepit/knowledge` is either a directory holding the docs or a file
/// holding the path to one. What must survive a pointer: the docs move, the
/// index follows them, and the pinned digest stays behind — CLAUDE.md imports
/// it by a path relative to the project root.
/// </summary>
public sealed class KnowledgeStoreLocationTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _firepitDir;
    private readonly string _shared;
    private readonly KnowledgeService _service;

    public KnowledgeStoreLocationTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-knowledge-store-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "public-repo");
        _firepitDir = Path.Combine(_project, ".firepit");
        _shared = Path.Combine(_root, ".firepit", "appkit");
        Directory.CreateDirectory(_firepitDir);
        Directory.CreateDirectory(_shared);

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

    private void WritePointer(string relativeTarget) =>
        File.WriteAllText(Path.Combine(_firepitDir, "knowledge"), relativeTarget);

    private static KnowledgeScopeRegistration Registration(string name, string project, string docsDir) =>
        new(name, project, KnowledgeStoreLocation.For(project, docsDir));

    // --- resolution ------------------------------------------------------

    [Fact]
    public void NoPointer_KeepsTheLayoutThatShippedBefore()
    {
        var resolved = KnowledgeLocator.Resolve(_project);
        Assert.False(resolved.IsRedirected);
        Assert.Null(resolved.Error);

        var location = KnowledgeStoreLocation.For(_project, resolved.DocsDir);
        Assert.Equal(Path.Combine(_firepitDir, "knowledge"), location.DocsDir);
        Assert.Equal(Path.Combine(_firepitDir, "knowledge.db"), location.IndexPath);
        Assert.Equal(Path.Combine(_firepitDir, "knowledge-pinned.md"), location.DigestPath);
    }

    [Fact]
    public void APointer_IsRelativeToTheFirepitDirectory()
    {
        WritePointer(@"..\..\.firepit\appkit");

        var resolved = KnowledgeLocator.Resolve(_project);
        Assert.True(resolved.IsRedirected);
        Assert.Null(resolved.Error);
        Assert.Equal(_shared, resolved.DocsDir);

        var location = KnowledgeStoreLocation.For(_project, resolved.DocsDir);
        Assert.Equal(_shared + ".db", location.IndexPath);
        // Stays behind: the CLAUDE.md import resolves against the project root.
        Assert.Equal(Path.Combine(_firepitDir, "knowledge-pinned.md"), location.DigestPath);
    }

    [Fact]
    public void APointer_MayCommentAndQuoteTheLine()
    {
        WritePointer("# knowledge lives in the private meta repo\n\"../../.firepit/appkit\"\n");

        Assert.Equal(_shared, KnowledgeLocator.Resolve(_project).DocsDir);
    }

    [Fact]
    public void APointerAtAPointer_IsRefusedRatherThanFollowed()
    {
        // One step, always. Refusing chains is what makes hop limits and cycle
        // detection unnecessary rather than merely unlikely to trigger.
        var other = Path.Combine(_root, "other", ".firepit");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "knowledge"), "../../.firepit/appkit");
        WritePointer(@"..\..\other\.firepit\knowledge");

        var resolved = KnowledgeLocator.Resolve(_project);
        Assert.NotNull(resolved.Error);
        Assert.Contains("another pointer", resolved.Error);
    }

    [Fact]
    public void AnEmptyPointer_SaysSoInsteadOfIndexingNothing()
    {
        WritePointer("   \n\n");

        Assert.Contains("is empty", KnowledgeLocator.Resolve(_project).Error);
    }

    [Fact]
    public void APointerIntoNowhere_IsAnErrorNotAnEmptyBase()
    {
        // A typo must not read as "this project has no knowledge yet" — that
        // is the silent-loss failure the pointer exists to prevent.
        WritePointer(@"..\..\.firepit\typo\deeper");

        Assert.Contains("does not exist", KnowledgeLocator.Resolve(_project).Error);
    }

    [Fact]
    public void APointerAtAnUncreatedDirectory_IsFineWhenItsParentExists()
    {
        WritePointer(@"..\..\.firepit\not-written-yet");

        var resolved = KnowledgeLocator.Resolve(_project);
        Assert.Null(resolved.Error);
        Assert.True(resolved.IsRedirected);
    }

    // --- behaviour -------------------------------------------------------

    [Fact]
    public async Task ARedirectedScope_WritesNothingIntoTheProjectItself()
    {
        WritePointer(@"..\..\.firepit\appkit");
        var docs = KnowledgeLocator.Resolve(_project).DocsDir;
        _service.SyncScopes([Registration("appkit", _project, docs)]);

        await _service.AddDocumentAsync("appkit", "Private Finding", "Body text.", pinned: false);

        Assert.Single(Directory.GetFiles(_shared, "*.md", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(_firepitDir, "knowledge")));
    }

    [Fact]
    public async Task ARedirectedScope_StillWritesTheDigestIntoTheProject()
    {
        WritePointer(@"..\..\.firepit\appkit");
        var docs = KnowledgeLocator.Resolve(_project).DocsDir;
        _service.SyncScopes([Registration("appkit", _project, docs)]);

        await _service.AddDocumentAsync("appkit", "Reflex Rule", "Always do this.", pinned: true);

        var digest = Path.Combine(_firepitDir, "knowledge-pinned.md");
        Assert.True(File.Exists(digest), $"expected a digest at {digest}");
        Assert.Contains("Always do this.", await File.ReadAllTextAsync(digest));
    }
}
