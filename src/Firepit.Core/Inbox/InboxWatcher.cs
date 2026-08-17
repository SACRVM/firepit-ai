using System.IO;

namespace Firepit.Core.Inbox;

/// <summary>
/// Watches <c>&lt;projectPath&gt;/.firepit/inbox/*.md</c> and surfaces two
/// distinct counts:
///
/// <list type="bullet">
///   <item><b>UnpendingCount</b> — every top-level <c>.md</c> file currently
///   sitting in the inbox folder. Files moved to <c>inbox/processed/</c>
///   stop counting. Drives the always-on toolbar Inbox button.</item>
///   <item><b>NewSinceSeenCount</b> — the subset of those files whose last-write
///   time is newer than the last <see cref="MarkAsSeen"/> call. Drives the
///   tab-header notification badge — "arrived while you were on another tab."
///   Resets to 0 when the user activates the tab.</item>
/// </list>
///
/// Marshals nothing — consumers hop dispatchers themselves if they touch UI.
/// </summary>
public sealed class InboxWatcher : IDisposable
{
    public const string InboxDirectory     = "inbox";
    public const string ProcessedDirectory = "processed";

    private readonly string _inboxPath;

    /// <summary>Serialises counting and the compare-and-set. See <see cref="Refresh"/>.</summary>
    private readonly object _gate = new();
    private readonly FileSystemWatcher? _fsw;
    private DateTime _seenAt = DateTime.UtcNow;
    private bool _disposed;

    public InboxWatcher(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ProjectPath = projectPath;
        _inboxPath  = Path.Combine(projectPath, ".firepit", InboxDirectory);

        try
        {
            Directory.CreateDirectory(_inboxPath);
            _fsw = new FileSystemWatcher(_inboxPath, "*.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _fsw.Created += (_, _) => Refresh();
            _fsw.Deleted += (_, _) => Refresh();
            _fsw.Renamed += (_, _) => Refresh();
            _fsw.EnableRaisingEvents = true;
        }
        catch
        {
            // Filesystem hostile (path doesn't exist, permission denied) —
            // watcher stays null. Initial count check below still runs.
        }

        Refresh();
    }

    public string ProjectPath        { get; }
    public string InboxPath          => _inboxPath;

    /// <summary>Total un-processed messages (drives the toolbar badge).</summary>
    public int    UnpendingCount     { get; private set; }

    /// <summary>Un-processed messages newer than the last <see cref="MarkAsSeen"/>
    /// (drives the tab-header notification badge).</summary>
    public int    NewSinceSeenCount  { get; private set; }

    /// <summary>Backwards-compat alias — equivalent to <see cref="UnpendingCount"/>.
    /// Kept so callers that haven't migrated to the split model still compile.</summary>
    public int    UnreadCount        => UnpendingCount;

    public event EventHandler<int>? UnpendingCountChanged;
    public event EventHandler<int>? NewSinceSeenCountChanged;

    /// <summary>Backwards-compat alias — fires alongside <see cref="UnpendingCountChanged"/>.</summary>
    public event EventHandler<int>? UnreadCountChanged;

    /// <summary>
    /// Called when the tab is activated. Treats everything currently in the
    /// inbox as "seen" — the new-counter snaps to zero and stays there until
    /// a fresh file arrives (mtime > now). The unpending counter is untouched
    /// because the user hasn't actually <i>processed</i> anything yet.
    /// </summary>
    public void MarkAsSeen()
    {
        var raise = false;
        lock (_gate)
        {
            _seenAt = DateTime.UtcNow;
            if (NewSinceSeenCount != 0)
            {
                NewSinceSeenCount = 0;
                raise = true;
            }
        }

        if (raise)
        {
            NewSinceSeenCountChanged?.Invoke(this, 0);
        }
    }

    /// <remarks>
    /// Counting and the compare-and-set happen under <see cref="_gate"/>. Two
    /// callers arrive routinely — the file watcher's thread and whoever asked
    /// explicitly — and unsynchronised they both read the old count, both find
    /// a delta, and both raise: one arrival, two events, and a badge that
    /// double-counts. Events are raised outside the lock so a handler is never
    /// called with it held.
    /// </remarks>
    public void Refresh()
    {
        try
        {
            int? raiseUnpending = null;
            int? raiseNewSince = null;

            lock (_gate)
            {
                int unpending = 0;
                int newSince = 0;
                if (Directory.Exists(_inboxPath))
                {
                    foreach (var path in Directory.EnumerateFiles(_inboxPath, "*.md", SearchOption.TopDirectoryOnly))
                    {
                        unpending++;
                        try
                        {
                            if (File.GetLastWriteTimeUtc(path) > _seenAt) newSince++;
                        }
                        catch
                        {
                            // Race with Move/Delete — count it as "new" rather than skip.
                            newSince++;
                        }
                    }
                }

                if (unpending != UnpendingCount)
                {
                    UnpendingCount = unpending;
                    raiseUnpending = unpending;
                }
                if (newSince != NewSinceSeenCount)
                {
                    NewSinceSeenCount = newSince;
                    raiseNewSince = newSince;
                }
            }

            if (raiseUnpending is { } u)
            {
                UnpendingCountChanged?.Invoke(this, u);
                UnreadCountChanged?.Invoke(this, u);
            }
            if (raiseNewSince is { } n)
            {
                NewSinceSeenCountChanged?.Invoke(this, n);
            }
        }
        catch
        {
            // Transient IO failure — leave counts alone, retry on next event.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_fsw is not null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } }
        catch { /* ignored */ }
    }
}
