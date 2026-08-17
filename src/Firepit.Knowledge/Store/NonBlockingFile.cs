using System.Security.Cryptography;

namespace Firepit.Knowledge.Store;

/// <summary>
/// Reads that never stand in a writer's way.
/// </summary>
/// <remarks>
/// <para>
/// <c>File.ReadAllBytes</c> and friends open with <c>FileShare.Read</c>, which
/// blocks anyone trying to write. For an index pass over a knowledge base that
/// is the wrong way round: the markdown is the truth and the index is derived
/// from it, so a pass that is merely reading must never be the reason a save
/// fails. Left as it was, a reindex running at the wrong moment made
/// <c>firepit_knowledge_update</c> throw "the process cannot access the file".
/// </para>
/// <para>
/// The cost is that a read concurrent with a write can see a half-written file.
/// That is recoverable and self-correcting: the hash of a torn read does not
/// match the finished file, the write updates the modification time, and the
/// watcher and the sweep both bring the next pass round again. A failed save is
/// not recoverable in the same way — the caller has already been told it did
/// not work.
/// </para>
/// </remarks>
public static class NonBlockingFile
{
    private const int BufferSize = 64 * 1024;

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);

    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public static string ReadAllText(string path)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Content hash without holding the file against its author.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
