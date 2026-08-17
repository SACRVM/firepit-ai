using Firepit.Knowledge.Store;

namespace Firepit.Knowledge.Tests;

/// <summary>
/// An index pass reads the documents it indexes. If those reads take an
/// exclusive-enough share, they block the author from saving — and the author
/// is the source of truth. This is the shape of the failure that reached CI:
/// <c>firepit_knowledge_update</c> threw "the process cannot access the file"
/// because a reindex happened to be reading it.
/// </summary>
public sealed class NonBlockingFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public NonBlockingFileTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(), "firepit-nonblocking", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "doc.md");
        File.WriteAllText(_file, "# Doc\n\noriginal\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheDefaultRead_BlocksAWriter()
    {
        // Documents the bug rather than the fix: this is what File.ReadAllBytes
        // does, and why the indexer must not use it.
        using var held = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<IOException>(() => File.WriteAllText(_file, "new content"));
    }

    [Fact]
    public async Task AReadInFlight_DoesNotStopASave()
    {
        await using var held = new FileStream(
            _file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        // The save has to go through while the index pass still holds the file.
        await File.WriteAllTextAsync(_file, "# Doc\n\nrewritten\n");

        Assert.Contains("rewritten", await File.ReadAllTextAsync(_file));
    }

    [Fact]
    public async Task ReadAllBytes_ReturnsTheContent()
    {
        var bytes = await NonBlockingFile.ReadAllBytesAsync(_file);

        Assert.Equal(File.ReadAllBytes(_file), bytes);
    }

    [Fact]
    public void ReadAllText_ReturnsTheContent()
    {
        Assert.Equal(File.ReadAllText(_file), NonBlockingFile.ReadAllText(_file));
    }

    [Fact]
    public async Task Hash_MatchesHashingTheBytes()
    {
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_file)));

        Assert.Equal(expected, await NonBlockingFile.HashAsync(_file));
    }

    [Fact]
    public async Task EveryHelper_LeavesTheFileWritable()
    {
        // The property that matters, asserted for each entry point rather than
        // trusted from one of them.
        await NonBlockingFile.ReadAllBytesAsync(_file);
        NonBlockingFile.ReadAllText(_file);
        await NonBlockingFile.HashAsync(_file);

        await File.WriteAllTextAsync(_file, "still writable");
        Assert.Equal("still writable", await File.ReadAllTextAsync(_file));
    }
}
