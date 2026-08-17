namespace Firepit.Knowledge.Tests;

/// <summary>
/// Every way a search can come back empty for a reason that is not "nothing
/// matched". These are the dangerous ones: the caller sees a normal, confident,
/// empty answer and concludes the subject is unknown.
/// </summary>
public sealed class KnowledgeSilentLossTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly KnowledgeService _service;

    public KnowledgeSilentLossTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-silent-loss", Guid.NewGuid().ToString("N"));
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

    private void Register() =>
        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);

    private async Task WaitUntilReady()
    {
        for (var i = 0; i < 60; i++)
        {
            var probe = await _service.SearchAsync("probe", ["project"], 1);
            if (probe.Trustworthy)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task AnUnknownScopeName_IsSaidOutLoudInsteadOfSearchedAsNothing()
    {
        Register();
        await WaitUntilReady();

        var result = await _service.SearchAsync("anything", ["typo-or-renamed"], 5);

        Assert.Empty(result.Hits);
        Assert.False(result.Trustworthy);
        Assert.Contains(result.Warnings!, w => w.Contains("typo-or-renamed"));
        // And it names what does exist, so the caller can correct itself.
        Assert.Contains(result.Warnings!, w => w.Contains("project"));
    }

    [Fact]
    public async Task OneUnknownScopeAmongGoodOnes_StillSearchesTheGoodOnes()
    {
        Directory.CreateDirectory(Docs);
        await File.WriteAllTextAsync(
            Path.Combine(Docs, "known.md"), "# Known\n\nA finding about widgets.\n");
        Register();
        await WaitUntilReady();

        var result = await _service.SearchAsync("widgets", ["project", "does-not-exist"], 5);

        Assert.NotEmpty(result.Hits);
        Assert.False(result.Trustworthy);
    }

    [Fact]
    public async Task AScopeThatHasNotIndexedYet_SaysSoRatherThanAnsweringEmpty()
    {
        // The startup window: registered, reindex still debouncing. A search
        // landing here would otherwise report a confident nothing.
        Directory.CreateDirectory(Docs);
        await File.WriteAllTextAsync(Path.Combine(Docs, "doc.md"), "# Doc\n\nContent.\n");
        Register();

        var result = await _service.SearchAsync("content", ["project"], 5);

        if (result.Hits.Count == 0)
        {
            Assert.False(result.Trustworthy);
            Assert.Contains(result.Warnings!, w => w.Contains("indexing"));
        }
    }

    [Fact]
    public async Task ADocumentLockedDuringAPass_IsNotLeftUnindexedForever()
    {
        // The subtlest one. A pass that skips a locked file used to record the
        // directory as fully indexed, so the sweep — which acts on drift —
        // never came back for it and the document stayed unfindable.
        Directory.CreateDirectory(Docs);
        var locked = Path.Combine(Docs, "locked.md");
        await File.WriteAllTextAsync(locked, "# Locked\n\nSomething about zebras.\n");

        using (var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Register();
            await Task.Delay(1500);

            var duringLock = await _service.SearchAsync("zebras", ["project"], 5);
            Assert.Empty(duringLock.Hits);
            // Crucially: not presented as "no such knowledge".
            Assert.False(duringLock.Trustworthy);
        }

        // Released — the retry has to find it without any file event, because
        // closing a handle raises none.
        for (var i = 0; i < 80; i++)
        {
            _service.SafetySweep();
            var after = await _service.SearchAsync("zebras", ["project"], 5);
            if (after.Hits.Count > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("the locked document was never picked up after the lock was released");
    }

    [Fact]
    public async Task AnEmptyBaseThatWasActuallySearched_ReportsNoWarnings()
    {
        // The control case: silence is only allowed to mean silence.
        Directory.CreateDirectory(Docs);
        await File.WriteAllTextAsync(Path.Combine(Docs, "doc.md"), "# Doc\n\nAbout widgets.\n");
        Register();
        await WaitUntilReady();

        var result = await _service.SearchAsync("nothing-like-this-exists-anywhere", ["project"], 5);

        Assert.Empty(result.Hits);
        Assert.True(result.Trustworthy);
    }
}
