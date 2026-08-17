namespace Firepit.Knowledge.Tests;

/// <summary>
/// The integrity check exists because every other mechanism is event-driven and
/// can fail quietly. These simulate that: the index and the documents are put
/// out of step behind the service's back, the way a lost watcher would.
/// </summary>
public sealed class KnowledgeIntegrityTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly KnowledgeService _service;

    public KnowledgeIntegrityTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-integrity", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
        _service = new KnowledgeService(modelDataRoot: Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Docs => KnowledgeLayout.LocalDocsDir(_project);

    private async Task<ScopeIntegrity> CheckAsync(bool repair = false) =>
        (await _service.CheckIntegrityAsync(["project"], repair)).Single();

    private async Task SeedAndIndex(params string[] names)
    {
        Directory.CreateDirectory(Docs);
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(
                Path.Combine(Docs, name), $"# {name}\n\nContent of {name}.\n");
        }

        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);
        await _service.AddDocumentAsync("project", "Seed", "Forces a synchronous index pass.");
    }

    [Fact]
    public async Task AScopeThatIsNotRegistered_IsReportedNotDropped()
    {
        // The empty-result-means-nothing-wrong shape. An unknown name used to
        // be filtered out, so the caller got an empty list and reported the
        // project sound — while every search against that name answered from
        // nothing. If scope sync fails, this is the state every project is in.
        await SeedAndIndex("one.md");

        var results = await _service.CheckIntegrityAsync(["project", "never-registered"]);

        var missing = Assert.Single(results, r => !r.IsRegistered);
        Assert.Equal("never-registered", missing.Scope);
        Assert.False(missing.Sound);
        Assert.Contains("no knowledge scope named", string.Join(" ", missing.Describe()));
        Assert.True(Assert.Single(results, r => r.Scope == "project").Sound);
    }

    [Fact]
    public async Task AFullyIndexedScope_IsSound()
    {
        await SeedAndIndex("one.md", "two.md");

        var result = await CheckAsync();

        Assert.True(result.Sound, string.Join("; ", result.Describe()));
        Assert.Empty(result.MissingFromIndex);
    }

    /// <summary>
    /// Puts the index out of step with the disk without touching a file.
    /// </summary>
    /// <remarks>
    /// Writing to the documents directory would be the more literal simulation,
    /// but the watcher is live in these tests and often repairs the drift before
    /// the check runs — which passed here and failed on CI, where the timing
    /// differs. The state under test is the disagreement itself, and this
    /// produces it deterministically: no file event, nothing to race.
    /// </remarks>
    private void EditIndex(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={KnowledgeStoreLocation.For(_project).IndexPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task ADocumentTheIndexNeverSaw_IsReportedAsUnfindable()
    {
        await SeedAndIndex("one.md", "invisible.md");

        // On disk, absent from the manifest — a lost watcher, a git checkout
        // while the app was busy, a share that reconnected.
        EditIndex("DELETE FROM documents WHERE path = 'invisible.md'");

        var result = await CheckAsync();

        Assert.False(result.Sound);
        Assert.Contains("invisible.md", result.MissingFromIndex);
        Assert.Contains(result.Describe(), d => d.Contains("cannot be found"));
    }

    [Fact]
    public async Task AChangedDocument_IsReportedAsAnsweringFromOldText()
    {
        await SeedAndIndex("one.md");

        // The file is what it always was; the index believes something else,
        // which is the state a half-finished pass leaves behind.
        EditIndex("UPDATE documents SET content_hash = 'stale' WHERE path = 'one.md'");

        var result = await CheckAsync();

        Assert.False(result.Sound);
        Assert.Contains("one.md", result.OutOfDate);
    }

    [Fact]
    public async Task Repair_BringsTheIndexBackInLineWithDisk()
    {
        await SeedAndIndex("one.md");
        await File.WriteAllTextAsync(
            Path.Combine(Docs, "invisible.md"), "# Invisible\n\nNever indexed.\n");
        File.Delete(Path.Combine(Docs, "one.md"));

        var repaired = await CheckAsync(repair: true);

        Assert.True(repaired.Sound, string.Join("; ", repaired.Describe()));
        Assert.Contains("reindexed", repaired.Repairs!);

        // And it is genuinely findable now, not merely accounted for.
        var found = await _service.SearchAsync("Invisible", ["project"], 5);
        Assert.NotEmpty(found.Hits);
    }

    [Fact]
    public async Task ACorruptIndex_IsReportedAndRebuiltFromTheDocuments()
    {
        await SeedAndIndex("one.md");
        var dbPath = KnowledgeStoreLocation.For(_project).IndexPath;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(dbPath, "this is not a database"u8.ToArray());

        var broken = await CheckAsync();
        Assert.False(broken.Sound);
        Assert.NotNull(broken.IndexError);

        // The index is derived, so throwing it away costs nothing the markdown
        // cannot replace — which is what makes automatic repair defensible.
        var fixedUp = await CheckAsync(repair: true);
        Assert.Null(fixedUp.IndexError);
        Assert.True(fixedUp.Sound, string.Join("; ", fixedUp.Describe()));

        var found = await _service.SearchAsync("one", ["project"], 5);
        Assert.NotEmpty(found.Hits);
    }

    [Fact]
    public async Task Repair_NeverDeletesADocument()
    {
        await SeedAndIndex("keep.md");
        var before = Directory.GetFiles(Docs, "*.md").Length;

        await CheckAsync(repair: true);

        Assert.Equal(before, Directory.GetFiles(Docs, "*.md").Length);
        Assert.True(File.Exists(Path.Combine(Docs, "keep.md")));
    }
}
