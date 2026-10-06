using Firepit.Knowledge.Indexing;
using Firepit.Knowledge.Search;
using Firepit.Knowledge.Store;
using Microsoft.Data.Sqlite;

namespace Firepit.Knowledge.Tests;

/// <summary>
/// The extension file going missing under a running Firepit: a temp cleaner
/// deleted the single-file exe's extracted <c>vec0.dll</c> between two
/// searches, and every search after that failed until a restart.
/// </summary>
public sealed class SqliteVecExtensionTests : IDisposable
{
    private readonly string _root;
    private readonly string _docs;
    private readonly string _db;
    private readonly FakeEmbeddingService _embeddings = new();

    public SqliteVecExtensionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-vec-tests", Guid.NewGuid().ToString("N"));
        _docs = Path.Combine(_root, ".firepit", "knowledge");
        _db = Path.Combine(_root, ".firepit", "knowledge.db");
        Directory.CreateDirectory(_docs);
        File.WriteAllText(
            Path.Combine(_docs, "conpty.md"),
            "# ConPTY resize quirks\n\nResizing the pseudo console mid-stream tears output.");
        File.WriteAllText(
            Path.Combine(_docs, "sqlite.md"),
            "# SQLite WAL mode\n\nWrite-ahead logging lets readers run beside one writer.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A pinned extension copy cannot be deleted while this process runs.
        }
    }

    private static string RealExtension()
    {
        Assert.Null(SqliteVecExtension.Default.CheckAvailable());
        return SqliteVecExtension.Default.LoadedFrom!;
    }

    private static string PlaceExtension(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SqliteVecExtension.FileName);
        File.Copy(RealExtension(), path);
        return path;
    }

    private async Task<KnowledgeSearchResult> SearchAsync(KnowledgeStore store)
    {
        // Every pooled connection closed first. SQLite frees the extension when
        // the connection that loaded it closes, and that is the moment the file
        // used to become deletable.
        SqliteConnection.ClearAllPools();
        return await new KnowledgeSearch(_embeddings).SearchAsync(store, "test", "ConPTY resize", 5);
    }

    [Fact]
    public async Task DeletingTheExtractedFileBetweenTwoSearches_DoesNotBreakTheSecond()
    {
        // Stands in for %TEMP%\.net\Firepit\<hash>\, outside the app directory.
        var extracted = PlaceExtension(Path.Combine(_root, "extracted"));
        var cache = Path.Combine(_root, "native");
        var vec = new SqliteVecExtension(extracted, cache);
        var store = new KnowledgeStore(_docs, _db, vec);
        await new KnowledgeIndexer(store, _embeddings).ReindexAsync();

        var first = await SearchAsync(store);
        Assert.False(first.Degraded, first.DegradedReason);
        Assert.Equal("conpty.md", first.Hits[0].Path);

        // Loaded from Firepit's own cache, not from where it was found.
        Assert.StartsWith(cache, vec.LoadedFrom);

        // What Storage Sense did: the extracted copy is not in use, so it goes.
        File.Delete(extracted);

        // The copy in use is pinned for the life of the process, and Windows
        // will not delete a file mapped as an image.
        Assert.ThrowsAny<UnauthorizedAccessException>(() => File.Delete(vec.LoadedFrom!));

        var second = await SearchAsync(store);
        Assert.False(second.Degraded, second.DegradedReason);
        Assert.Equal(first.Hits.Select(h => h.Path), second.Hits.Select(h => h.Path));
    }

    [Fact]
    public async Task WithTheExtensionGone_SearchAnswersFromFullText_AndRecoversWhenItReturns()
    {
        await new KnowledgeIndexer(new KnowledgeStore(_docs, _db), _embeddings).ReindexAsync();

        var dir = Path.Combine(_root, "extracted");
        var vec = new SqliteVecExtension(Path.Combine(dir, SqliteVecExtension.FileName), cacheDir: null);
        var store = new KnowledgeStore(_docs, _db, vec);

        // Used to be "SQLite Error 1: 'The specified module could not be
        // found.'" for the whole search.
        var without = await SearchAsync(store);
        Assert.True(without.Degraded);
        Assert.Contains("sqlite-vec", without.DegradedReason);
        Assert.Equal("conpty.md", without.Hits[0].Path);

        // No restart: a failed load looks for the file again.
        PlaceExtension(dir);
        var with = await SearchAsync(store);
        Assert.False(with.Degraded, with.DegradedReason);
        Assert.Equal("conpty.md", with.Hits[0].Path);
    }

    [Fact]
    public async Task WithTheExtensionGone_NothingIsWrittenToTheIndex()
    {
        var vec = new SqliteVecExtension(
            Path.Combine(_root, "nowhere", SqliteVecExtension.FileName), cacheDir: null);

        // A chunk rewritten without its vector leaves the old vector behind,
        // and the next write of that chunk collides with it. So no writes.
        await Assert.ThrowsAsync<SqliteVecUnavailableException>(
            () => new KnowledgeIndexer(new KnowledgeStore(_docs, _db, vec), _embeddings).ReindexAsync());
    }

    [Fact]
    public void TheCacheIsNamedByContent_SoACopyIsReused()
    {
        var extracted = PlaceExtension(Path.Combine(_root, "extracted"));
        var cache = Path.Combine(_root, "native");

        var first = SqliteVecExtension.CopyToCache(extracted, cache);
        var second = SqliteVecExtension.CopyToCache(extracted, cache);

        Assert.Equal(first, second);
        Assert.Equal(File.ReadAllBytes(extracted), File.ReadAllBytes(first));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first)!));
    }
}
