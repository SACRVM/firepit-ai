using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Firepit.Core.Inbox;
using Firepit.Native;
using Serilog;

namespace Firepit.Views;

/// <summary>
/// Inbox triage. Every pending message is listed at once — sender, subject,
/// age — because that is what a decision needs; the body is there to look at,
/// not to read before acting.
///
/// The primary action hands the whole queue to the project's agent in one
/// prompt, replacing the "work your inbox" the user otherwise types by hand.
/// The prompt carries the standing safety rule: act, but stop and ask before
/// anything irreversible. That gate deliberately lives in the prompt and not
/// in Firepit — deciding whether an instruction is destructive means reading
/// the message, which the host does not do, and the receiving agent is the
/// only party that can actually judge it.
///
/// Filesystem is the source of truth. We load once on Show — if a new message
/// arrives while the window is open, the user closes and reopens. Good enough
/// for the realistic ~3-message scale; can be promoted to a live watcher later.
/// </summary>
public partial class InboxWindow : Window
{
    private readonly string _projectName;
    private readonly string _inboxDir;
    private readonly System.Action<string> _sendToPty;
    private readonly ObservableCollection<InboxRow> _rows = new();

    private InboxWindow(string projectName, string projectPath, System.Action<string> sendToPty)
    {
        InitializeComponent();
        _projectName = projectName;
        _inboxDir    = Path.Combine(projectPath, ".firepit", "inbox");
        _sendToPty   = sendToPty;

        MessageList.ItemsSource = _rows;

        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;

            // capH is 32 * fontScale (see App.ApplyFontResources). The action
            // row's buttons grow with BaseFontSize, so at larger font settings
            // the fixed window size clips them. Grow by the same scale to keep
            // every button on-screen. Only scale up — at smaller fonts the
            // default size is already roomy.
            var scale = capH / 32.0;
            if (scale > 1.0)
            {
                Width     *= scale;
                Height    *= scale;
                MinWidth  *= scale;
                MinHeight *= scale;
            }
        }
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Open inbox triage for <paramref name="projectName"/>. Returns
    /// immediately if the inbox is empty — caller can check via
    /// <see cref="HasMessages"/> on the supplied path before calling.
    /// </summary>
    public static void Show(
        Window owner,
        string projectName,
        string projectPath,
        System.Action<string> sendToPty)
    {
        var win = new InboxWindow(projectName, projectPath, sendToPty)
        {
            Owner = owner,
        };
        // The font-scaled size in the ctor can still exceed a small screen.
        DialogSizing.ClampToScreen(win);
        win.LoadMessages();
        if (win._rows.Count == 0)
        {
            // Race vs. the toolbar-button's count: the count's source-of-truth
            // is the file watcher, but a file could vanish between the click
            // and Show. Don't pop an empty window.
            MessageDialog.Show(owner,
                title: "Inbox empty",
                message: $"No pending messages in {projectName}'s inbox.",
                primaryLabel: "OK");
            return;
        }
        win.MessageList.SelectedIndex = 0;
        win.ShowDialog();
    }

    /// <summary>Cheap pre-check the toolbar can use before deciding whether to
    /// open the window vs. show a "no messages" toast.</summary>
    public static bool HasMessages(string projectPath)
    {
        var dir = Path.Combine(projectPath, ".firepit", "inbox");
        if (!Directory.Exists(dir)) return false;
        try
        {
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException) { return false; }
    }

    private void LoadMessages()
    {
        _rows.Clear();
        if (!Directory.Exists(_inboxDir))
        {
            RefreshChrome();
            return;
        }

        var items = new List<InboxItem>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_inboxDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var parsed = InboxFrontmatterParser.Parse(raw);
                    items.Add(new InboxItem(
                        Id:       Path.GetFileName(file),
                        FullPath: file,
                        From:     parsed.Frontmatter.GetValueOrDefault("from"),
                        Subject:  parsed.Frontmatter.GetValueOrDefault("subject"),
                        Priority: parsed.Frontmatter.GetValueOrDefault("priority"),
                        SentAt:   parsed.Frontmatter.GetValueOrDefault("sentAt")
                                  ?? parsed.Frontmatter.GetValueOrDefault("date"),
                        Body:     parsed.Body));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "InboxWindow: couldn't read {File}", file);
                }
            }
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "InboxWindow: enumerate failed for {Dir}", _inboxDir);
        }

        // Filenames start with an ISO date in the firepit_send_to convention,
        // so ordinal sort puts oldest first — natural read order.
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        foreach (var item in items)
        {
            _rows.Add(new InboxRow(item));
        }
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        CaptionText.Text = _rows.Count == 1
            ? $"Inbox · {_projectName} · 1 message"
            : $"Inbox · {_projectName} · {_rows.Count} messages";

        // One message is not a queue — say what the button will actually do.
        ProcessLabel.Text = _rows.Count == 1 ? "Process" : "Process all";

        var hasSelection = MessageList.SelectedItem is InboxRow;
        ProcessButton.IsEnabled  = _rows.Count > 0;
        MarkDoneButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled   = hasSelection;
    }

    private void OnMessageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        BodyText.Text = (MessageList.SelectedItem as InboxRow)?.Item.Body ?? string.Empty;
        // Every message starts at the top. Without this, picking a row after
        // scrolling drops you into the middle of the next one.
        BodyText.CaretIndex = 0;
        BodyText.ScrollToHome();
        RefreshChrome();
    }

    /// <summary>
    /// Hand the queue to the agent. One prompt for the whole inbox rather than
    /// one per message: the agent can read the folder itself, and a single
    /// injection keeps the standing safety rule stated exactly once.
    /// </summary>
    private void OnProcessClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;

        // A file REFERENCE, never the body: long multi-line pastes through the
        // PTY arrive truncated in the agent's prompt (observed in the field —
        // only the tail survived). The files are already on disk in the project
        // the session runs in, so a pointer is lossless.
        var prompt = _rows.Count == 1
            ? $"Work the message .firepit/inbox/{_rows[0].Item.Id} in your Firepit inbox: "
            : "Work your Firepit inbox: read every pending message in `.firepit/inbox/*.md` in full, ";
        prompt +=
            "act on it, then mark it done with the firepit_inbox_complete MCP tool "
            + "(id = the message's filename). Stop and ask me first before anything "
            + "irreversible — deleting or overwriting files, force-pushing, cutting a "
            + "release, or sending anything outside this machine.";

        try { _sendToPty(prompt); }
        catch (Exception ex)
        {
            Log.Warning(ex, "InboxWindow: send-to-PTY failed");
            MessageDialog.Show(this,
                title: "Couldn't reach the session",
                message: ex.Message,
                primaryLabel: "OK");
            return;
        }

        Log.Information(
            "Inbox: handed {Count} message(s) to the agent in {Project}", _rows.Count, _projectName);
        // The agent marks each one done via MCP as it finishes; the files stay
        // put until then. Nothing left for the user to do here.
        Close();
    }

    private void OnMarkDoneClick(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not InboxRow row) return;
        var msg = row.Item;

        var processedDir = Path.Combine(_inboxDir, "processed");
        var target       = Path.Combine(processedDir, msg.Id);
        try
        {
            Directory.CreateDirectory(processedDir);
            if (File.Exists(target))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
                var ext   = Path.GetExtension(msg.Id);
                var stem  = Path.GetFileNameWithoutExtension(msg.Id);
                target    = Path.Combine(processedDir, $"{stem}-{stamp}{ext}");
            }
            File.Move(msg.FullPath, target);
            Log.Information("Inbox: marked '{Id}' done in {Project}", msg.Id, _projectName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Inbox: mark-done failed for {Id}", msg.Id);
            MessageDialog.Show(this,
                title: "Could not mark as done",
                message: ex.Message,
                primaryLabel: "OK");
            return;
        }

        RemoveRow(row);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not InboxRow row) return;
        var msg = row.Item;

        var confirmed = MessageDialog.Show(this,
            title: "Delete this message?",
            message: $"Permanently delete \"{msg.Subject ?? "(no subject)"}\" from {_projectName}'s inbox?",
            primaryLabel: "Delete",
            secondaryLabel: "Cancel");
        if (!confirmed) return;

        try
        {
            File.Delete(msg.FullPath);
            Log.Information("Inbox: deleted '{Id}' from {Project}", msg.Id, _projectName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Inbox: delete failed for {Id}", msg.Id);
            MessageDialog.Show(this,
                title: "Could not delete",
                message: ex.Message,
                primaryLabel: "OK");
            return;
        }

        RemoveRow(row);
    }

    private void RemoveRow(InboxRow row)
    {
        var index = _rows.IndexOf(row);
        _rows.Remove(row);
        if (_rows.Count == 0)
        {
            Close();
            return;
        }
        MessageList.SelectedIndex = Math.Clamp(index, 0, _rows.Count - 1);
        RefreshChrome();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>List row: the triage surface, derived once at load time.</summary>
    private sealed class InboxRow
    {
        public InboxRow(InboxItem item)
        {
            Item    = item;
            From    = string.IsNullOrWhiteSpace(item.From) ? "(unknown)" : item.From;
            Subject = string.IsNullOrWhiteSpace(item.Subject) ? "(no subject)" : item.Subject;
            Age     = FormatAge(item.SentAt);

            var dot = item.Priority?.ToLowerInvariant() switch
            {
                "high" => Color.FromRgb(0xE5, 0x8A, 0x78),
                "low"  => Color.FromRgb(0x5A, 0x52, 0x47),
                _      => Color.FromRgb(0xA8, 0x9F, 0x92),
            };
            var brush = new SolidColorBrush(dot);
            brush.Freeze();
            DotBrush = brush;
        }

        public InboxItem Item { get; }
        public string From { get; }
        public string Subject { get; }
        public string Age { get; }
        public Brush DotBrush { get; }

        /// <summary>
        /// Relative age beats a timestamp here: the only question a reader has
        /// is "how stale is this", and "3h" answers it without arithmetic.
        /// </summary>
        private static string FormatAge(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !DateTimeOffset.TryParse(raw, out var sent))
            {
                return string.Empty;
            }
            var span = DateTimeOffset.Now - sent;
            if (span < TimeSpan.Zero)      return "now";
            if (span < TimeSpan.FromMinutes(1)) return "now";
            if (span < TimeSpan.FromHours(1))   return $"{(int)span.TotalMinutes}m";
            if (span < TimeSpan.FromDays(1))    return $"{(int)span.TotalHours}h";
            return $"{(int)span.TotalDays}d";
        }
    }

    private sealed record InboxItem(
        string Id,
        string FullPath,
        string? From,
        string? Subject,
        string? Priority,
        string? SentAt,
        string Body);
}
