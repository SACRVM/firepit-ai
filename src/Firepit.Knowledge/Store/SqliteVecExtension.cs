using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Firepit.Knowledge.Store;

/// <summary>
/// The sqlite-vec extension (<c>vec0.dll</c>): where its file is, and keeping
/// it there for as long as the process runs.
/// </summary>
/// <remarks>
/// <para>
/// The binary ships in the <c>sqlite-vec</c> NuGet package. A normal build
/// finds it under <c>runtimes/{rid}/native/</c> beside the app. The published
/// single-file exe self-extracts its natives to
/// <c>%TEMP%\.net\Firepit\&lt;hash&gt;\</c> instead, and that is where it went
/// wrong: SQLite loads an extension per connection and frees it again when the
/// connection closes, so between two searches <c>vec0.dll</c> was the one
/// native in that folder nobody held open. A Storage Sense pass over
/// <c>%TEMP%</c> deleted it, and from then on every connection failed to open
/// with "The specified module could not be found" until Firepit restarted.
/// </para>
/// <para>
/// Three defences, each enough for the observed failure on its own:
/// </para>
/// <list type="number">
///   <item><b>Out of the temp directory.</b> A copy found outside the app's own
///   directory is copied into a cache under Firepit's local data, in a folder
///   named after its content hash, and loaded from there.</item>
///   <item><b>Pinned.</b> The file is loaded once with
///   <see cref="NativeLibrary"/> and never freed. A connection's load then only
///   adds a reference to a module already in memory, and Windows does not
///   delete a file that is mapped as an image.</item>
///   <item><b>Resolved again on failure.</b> A load that fails looks for the
///   file afresh — re-copying a cache copy that has gone — and tries once
///   more before giving up.</item>
/// </list>
/// <para>
/// When all of that fails the caller gets
/// <see cref="SqliteVecUnavailableException"/>. Searches then answer from
/// full-text alone rather than not at all.
/// </para>
/// </remarks>
public sealed class SqliteVecExtension
{
    public const string FileName = "vec0.dll";

    private readonly Lock _gate = new();
    private readonly string? _sourceOverride;
    private readonly string? _cacheDir;
    private readonly List<nint> _pins = [];
    private string? _path;
    private string? _lastError;

    /// <summary>
    /// Finds the extension where the runtime puts it and loads it from there.
    /// For callers with nowhere to cache a copy — tests, tools.
    /// </summary>
    public static SqliteVecExtension Default { get; } = new(sourceOverride: null, cacheDir: null);

    /// <summary>
    /// Like <see cref="Default"/>, but a copy found outside the app's directory
    /// is moved into <paramref name="cacheDir"/> first.
    /// </summary>
    public static SqliteVecExtension WithCache(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        return new SqliteVecExtension(sourceOverride: null, cacheDir);
    }

    /// <param name="sourceOverride">Use this file as the source instead of
    /// probing the runtime's directories. For tests.</param>
    /// <param name="cacheDir">Where to copy a source that sits outside the
    /// app's directory; null loads it where it is.</param>
    internal SqliteVecExtension(string? sourceOverride, string? cacheDir)
    {
        _sourceOverride = sourceOverride is null ? null : Path.GetFullPath(sourceOverride);
        _cacheDir = cacheDir is null ? null : Path.GetFullPath(cacheDir);
    }

    /// <summary>The file the extension is loaded from, once it has been found.</summary>
    public string? LoadedFrom
    {
        get
        {
            lock (_gate)
            {
                return _path;
            }
        }
    }

    /// <summary>
    /// Loads the extension into a throwaway in-memory connection. Null when it
    /// works, otherwise why not. Also what pins the file, so calling it at
    /// startup closes the window in which a cleaner could get to it.
    /// </summary>
    public string? CheckAvailable()
    {
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            LoadInto(conn);
            return null;
        }
        catch (SqliteVecUnavailableException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Loads the extension into <paramref name="connection"/>.</summary>
    /// <exception cref="SqliteVecUnavailableException">It could not be loaded,
    /// even after looking for the file again.</exception>
    public void LoadInto(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string? error = null;
        if (Resolve(again: false) is { } path && TryLoad(connection, path, out error))
        {
            return;
        }

        if (Resolve(again: true) is { } retry && TryLoad(connection, retry, out error))
        {
            return;
        }

        string? lastError;
        string? lastPath;
        lock (_gate)
        {
            lastError = error ?? _lastError;
            lastPath = _path;
        }

        throw new SqliteVecUnavailableException(lastPath is null
            ? $"the sqlite-vec extension ({FileName}) could not be found: {lastError}"
            : $"the sqlite-vec extension could not be loaded from {lastPath}: {lastError}");
    }

    private static bool TryLoad(SqliteConnection connection, string path, out string? error)
    {
        connection.EnableExtensions(true);
        try
        {
            connection.LoadExtension(path);
            error = null;
            return true;
        }
        catch (SqliteException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            // Back off once vec0 is in, as Microsoft.Data.Sqlite recommends:
            // keeps ad-hoc load_extension() calls from untrusted SQL blocked
            // for the rest of the connection's life.
            connection.EnableExtensions(false);
        }
    }

    /// <summary>
    /// The file to load, pinned. <paramref name="again"/> ignores what was
    /// found before and looks afresh — the self-heal after a failed load.
    /// </summary>
    private string? Resolve(bool again)
    {
        lock (_gate)
        {
            if (!again && _path is not null)
            {
                return _path;
            }

            var source = FindSource();
            if (source is null)
            {
                // A copy already in the cache outlives the source it was made
                // from, which is the point of having it.
                if (_path is not null && File.Exists(_path))
                {
                    return _path;
                }

                _lastError =
                    $"{FileName} is not next to the app or in any of the runtime's native library directories";
                return null;
            }

            var target = source;
            if (_cacheDir is not null && !IsUnder(AppContext.BaseDirectory, source))
            {
                try
                {
                    target = CopyToCache(source, _cacheDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Loading from where the runtime put it is still better
                    // than not loading; the pin keeps that copy in place too.
                    _lastError = $"could not copy {source} to {_cacheDir}: {ex.Message}";
                }
            }

            if (!NativeLibrary.TryLoad(target, out var handle))
            {
                _lastError = $"{target} exists but is not a loadable library";
                return null;
            }

            // Never freed. Loading the same file again only adds a reference,
            // so keeping every handle costs nothing and releasing one could
            // unload a module that a connection still has registered.
            _pins.Add(handle);
            _path = target;
            return target;
        }
    }

    private string? FindSource()
    {
        if (_sourceOverride is not null)
        {
            return File.Exists(_sourceOverride) ? _sourceOverride : null;
        }

        var dirs = new List<string> { AppContext.BaseDirectory };

        // The runtime's own list: every directory deps.json places a native
        // asset in, and in a single-file app the folder the bundle extracted
        // to. Microsoft.Data.Sqlite probes the same list when handed a bare
        // extension name.
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirs)
        {
            dirs.AddRange(searchDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        dirs.Add(Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <c>&lt;cacheDir&gt;/&lt;hash&gt;/vec0.dll</c>
    /// unless that copy exists already. Named by content, so two Firepit
    /// versions shipping different builds never share a file, and two running
    /// instances shipping the same one do.
    /// </summary>
    internal static string CopyToCache(string source, string cacheDir)
    {
        var bytes = File.ReadAllBytes(source);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        var dir = Path.Combine(cacheDir, hash);
        var target = Path.Combine(dir, FileName);
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"{FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, target);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another instance got there first with the same bytes.
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return target;
    }

    private static bool IsUnder(string dir, string path)
    {
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// sqlite-vec could not be loaded. Vector search is unavailable; full-text
/// search still works, since FTS5 is built into SQLite itself.
/// </summary>
public sealed class SqliteVecUnavailableException(string message) : Exception(message);
