using Firepit.Knowledge.Store;
using Microsoft.Data.Sqlite;

namespace Firepit.Knowledge.Tests;

/// <summary>
/// What the service does when a base cannot be searched properly: the search
/// result, the integrity check and its repair all have to tell the truth about
/// it, and none of them may make it worse.
/// </summary>
public sealed class KnowledgeVectorFallbackTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;

    public KnowledgeVectorFallbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-vec-fallback", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string DbPath => KnowledgeStoreLocation.For(_project).IndexPath;

    private KnowledgeService NewService(SqliteVecExtension? vec = null) =>
        new(Path.Combine(_root, "data"), loggerFactory: null, vec);

    private SqliteVecExtension Missing() =>
        new(Path.Combine(_root, "nowhere", SqliteVecExtension.FileName), cacheDir: null);

    private async Task SeedAsync()
    {
        using (var svc = NewService())
        {
            svc.SyncScopes([new KnowledgeScopeRegistration("project", _project)]);
            await svc.AddDocumentAsync(
                "project", "ConPTY resize quirks", "Resizing the pseudo console mid-stream tears output.");
            await svc.WaitForPendingWorkAsync();
        }

        // Closing the last connection checkpoints the WAL into the file.
        SqliteConnection.ClearAllPools();
    }

    private static async Task<KnowledgeService> Registered(KnowledgeService svc, string project)
    {
        svc.SyncScopes([new KnowledgeScopeRegistration("project", project)]);
        await svc.WaitForPendingWorkAsync();
        return svc;
    }

    [Fact]
    public async Task AMissingExtension_StillAnswersFromFullText()
    {
        await SeedAsync();
        using var svc = await Registered(NewService(Missing()), _project);

        var result = await svc.SearchAsync("ConPTY", ["project"]);

        Assert.NotEmpty(result.Hits);
        Assert.True(result.Degraded);
        Assert.Contains("sqlite-vec", result.DegradedReason);
        Assert.False(result.NothingSearched);
    }

    [Fact]
    public async Task AMissingExtension_IsReportedAsTheRuntime_AndRepairLeavesTheIndexAlone()
    {
        await SeedAsync();
        var before = await File.ReadAllBytesAsync(DbPath);
        using var svc = await Registered(NewService(Missing()), _project);

        var result = (await svc.CheckIntegrityAsync(["project"], repair: true)).Single();

        // Not "index unreadable", and nothing recommending the repair that
        // destroyed the index last time.
        Assert.NotNull(result.ExtensionError);
        Assert.Null(result.IndexError);
        Assert.False(result.Sound);
        Assert.Contains(result.Describe(), d => d.Contains("restart Firepit"));
        Assert.Empty(result.MissingFromIndex);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(DbPath));
    }

    [Fact]
    public async Task ASearchWhereEveryBaseFailed_IsDegradedAndSaysNothingWasSearched()
    {
        await SeedAsync();
        await File.WriteAllBytesAsync(DbPath, "this is not a database"u8.ToArray());
        using var svc = await Registered(NewService(), _project);

        var result = await svc.SearchAsync("ConPTY", ["project"]);

        Assert.Empty(result.Hits);
        Assert.True(result.Degraded);
        Assert.True(result.NothingSearched);
        Assert.Contains(result.Warnings!, w => w.Contains("could not be searched"));
    }

    [Fact]
    public async Task ARebuildThatFails_LeavesTheOldIndexInPlace()
    {
        await SeedAsync();
        var unreadable = "this is not a database"u8.ToArray();
        await File.WriteAllBytesAsync(DbPath, unreadable);

        // A directory where the rebuild wants its scratch file: a way to make
        // the rebuild fail that leaves everything else working.
        Directory.CreateDirectory(DbPath + ".rebuild");
        using var svc = await Registered(NewService(), _project);

        var result = (await svc.CheckIntegrityAsync(["project"], repair: true)).Single();

        Assert.Contains(result.Repairs!, r => r.Contains("left in place"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(unreadable, await File.ReadAllBytesAsync(DbPath));
    }
}
