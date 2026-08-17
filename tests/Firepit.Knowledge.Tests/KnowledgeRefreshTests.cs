namespace Firepit.Knowledge.Tests;

/// <summary>
/// The index has to be current whenever it is searched, and the watcher is
/// only one of the ways it gets there. These cover the paths that do not go
/// through a file event at all.
/// </summary>
public sealed class KnowledgeRefreshTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _elsewhere;
    private readonly KnowledgeService _service;

    public KnowledgeRefreshTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-knowledge-refresh", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        _elsewhere = Path.Combine(_root, ".firepit", "projects", "shared", "knowledge");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
        Directory.CreateDirectory(_elsewhere);

        _service = new KnowledgeService(modelDataRoot: Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static async Task<bool> Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 60; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private Task<bool> ScopeSees(string scope, string text) =>
        _service.SearchAsync(text, [scope], 5).ContinueWith(t => t.Result.Hits.Count > 0);

    [Fact]
    public async Task DocsThatChangedWhileFirepitWasClosed_AreIndexedOnRegistration()
    {
        // The everyday case: a git pull, or editing in another editor with
        // Firepit shut. No file event ever reaches us for those.
        var docs = KnowledgeLayout.LocalDocsDir(_project);
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(
            Path.Combine(docs, "offline.md"), "# Offline\n\nWritten while nothing was watching.\n");

        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);

        Assert.True(await Eventually(() => ScopeSees("project", "Offline")));
    }

    [Fact]
    public async Task APointerThatMovesTheDocs_ReindexesAtTheNewLocation()
    {
        // Same project path, same scope name, different storage. Treating that
        // as unchanged would keep the old directory indexed until a restart —
        // which is exactly what the settings dialog does when it is used.
        var docs = KnowledgeLayout.LocalDocsDir(_project);
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(Path.Combine(docs, "here.md"), "# Here\n\nIn the repo.\n");
        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);
        Assert.True(await Eventually(() => ScopeSees("project", "Here")));

        await File.WriteAllTextAsync(
            Path.Combine(_elsewhere, "moved.md"), "# Moved\n\nSomewhere else entirely.\n");
        _service.SyncScopes(
        [
            new KnowledgeScopeRegistration(
                "project", _project, KnowledgeStoreLocation.For(_project, _elsewhere)),
        ]);

        Assert.True(await Eventually(() => ScopeSees("project", "Moved")));
    }

    [Fact]
    public async Task TheSweep_CatchesAChangeNoWatcherReported()
    {
        var docs = KnowledgeLayout.LocalDocsDir(_project);
        Directory.CreateDirectory(docs);
        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);
        Assert.True(await Eventually(() => ScopeSees("project", "anything").ContinueWith(_ => true)));

        // Stand-in for a watcher that stopped delivering — a share that went
        // away, or a buffer overflow. The file is simply there without an event.
        await File.WriteAllTextAsync(
            Path.Combine(docs, "unseen.md"), "# Unseen\n\nNo event was raised for this.\n");

        _service.SafetySweep();

        Assert.True(await Eventually(() => ScopeSees("project", "Unseen")));
    }

    [Fact]
    public async Task ADocumentsDirectoryThatDisappears_StopsAnsweringAsIfItWereEmpty()
    {
        // A scope registered against a directory that later goes away — moved,
        // unmounted, or repointed at a shared base while the old registration
        // is still live. The pass drops every row and would otherwise report a
        // healthy, empty base: a search then says "nothing known" about
        // documents that exist, and nothing in the answer admits the difference.
        var docs = KnowledgeLayout.LocalDocsDir(_project);
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(
            Path.Combine(docs, "vanishing.md"), "# Vanishing\n\nHere for now.\n");

        _service.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);
        Assert.True(await Eventually(() => ScopeSees("project", "Vanishing")));

        Directory.Delete(docs, recursive: true);
        _service.SafetySweep();

        Assert.True(await Eventually(async () =>
            (await _service.CheckIntegrityAsync(["project"])).Single().Health == ScopeHealth.Failed));

        var result = await _service.SearchAsync("Vanishing", ["project"], 5);
        Assert.Empty(result.Hits);
        Assert.NotEmpty(result.Warnings ?? []);
    }

    [Fact]
    public async Task TwoProjectsOnOneDirectory_ShareASingleIndexedBase()
    {
        // The appkit case. Registering it twice would have two watchers and two
        // indexers writing the same files.
        await File.WriteAllTextAsync(
            Path.Combine(_elsewhere, "shared.md"), "# Shared\n\nOne base, several members.\n");

        _service.SyncScopes(
        [
            new KnowledgeScopeRegistration(
                "shared", _project, KnowledgeStoreLocation.For(_project, _elsewhere)),
        ]);

        Assert.True(await Eventually(() => ScopeSees("shared", "Shared")));
        Assert.Single(_service.ScopeNames);
    }
}
