using System.Collections.Generic;
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
/// Wizard-style inbox browser. Shows one message at a time with Prev/Next,
/// plus three actions: Send to Claude (sends a one-line file-reference prompt
/// into the active PTY — the agent reads the message file itself),
/// Mark done (moves the file to <c>.firepit/inbox/processed/</c>) and
/// Delete (removes the file outright, with a confirm). All three auto-advance
/// to the next message; the window closes when the queue runs dry.
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
    private readonly List<InboxItem> _messages = new();
    private int _index;

    private InboxWindow(string projectName, string projectPath, System.Action<string> sendToPty)
    {
        InitializeComponent();
        _projectName = projectName;
        _inboxDir    = Path.Combine(projectPath, ".firepit", "inbox");
        _sendToPty   = sendToPty;

        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;

            // capH is 32 * fontScale (see App.ApplyFontResources). The action
            // row's buttons grow with BaseFontSize, so at larger font settings
            // the fixed 560x460 window clips them. Grow the window by the same
            // scale to keep every button on-screen. Only scale up — at smaller
            // fonts the default size is already roomy.
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
    /// Open the inbox wizard for <paramref name="projectName"/>. Returns
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
        if (win._messages.Count == 0)
        {
            // Race vs. the toolbar-button's count: the count's source-of-truth
            // is the file watcher, but a file could vanish between the click
            // and Show. Don't pop an empty wizard.
            MessageDialog.Show(owner,
                title: "Inbox empty",
                message: $"No pending messages in {projectName}'s inbox.",
                primaryLabel: "OK");
            return;
        }
        win.RenderCurrent();
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
        _messages.Clear();
        if (!Directory.Exists(_inboxDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_inboxDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var parsed = InboxFrontmatterParser.Parse(raw);
                    _messages.Add(new InboxItem(
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
        _messages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        _index = 0;
    }

    private void RenderCurrent()
    {
        if (_messages.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Clamp(_index, 0, _messages.Count - 1);
        var msg = _messages[_index];

        CaptionText.Text  = $"Inbox · {_projectName}";
        PositionText.Text = $"{_index + 1} / {_messages.Count}";

        FromText.Text    = string.IsNullOrWhiteSpace(msg.From)    ? "(unknown)" : msg.From;
        SubjectText.Text = string.IsNullOrWhiteSpace(msg.Subject) ? "(no subject)" : msg.Subject;
        BodyText.Text    = msg.Body ?? string.Empty;
        // Every message starts at the top. Without this, paging on from a
        // scrolled-down message drops you into the middle of the next one.
        BodyText.CaretIndex = 0;
        BodyText.ScrollToHome();

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(msg.Priority)) metaParts.Add($"priority: {msg.Priority}");
        if (!string.IsNullOrWhiteSpace(msg.SentAt))   metaParts.Add(FormatSentAt(msg.SentAt));
        MetaText.Text = string.Join("  ·  ", metaParts);

        PriorityDot.Foreground = msg.Priority?.ToLowerInvariant() switch
        {
            "high" => new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x78)),
            "low"  => new SolidColorBrush(Color.FromRgb(0x5A, 0x52, 0x47)),
            _      => new SolidColorBrush(Color.FromRgb(0xA8, 0x9F, 0x92)),
        };

        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _messages.Count - 1;
    }

    private static string FormatSentAt(string raw)
    {
        // Try ISO first ("2026-06-12T14:23:45.123Z"); fall back to verbatim.
        if (DateTimeOffset.TryParse(raw, out var dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        return raw;
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_index > 0) { _index--; RenderCurrent(); }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_index < _messages.Count - 1) { _index++; RenderCurrent(); }
    }

    private void OnSendToClaudeClick(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        var msg = _messages[_index];

        // Send a file REFERENCE, not the body: long multi-line pastes through
        // the PTY arrive truncated/mangled in the agent's prompt (observed in
        // the field — only the tail survived). The message file is already on
        // disk in the project the session runs in, so a one-line pointer is
        // lossless and the agent reads the full frontmatter+body itself.
        var prompt =
            $"Inbox message from {msg.From ?? "(unknown)"} — \"{msg.Subject ?? "(no subject)"}\". " +
            $"Read .firepit/inbox/{msg.Id} in full, act on it, then call " +
            $"firepit_inbox_complete with id=\"{msg.Id}\".";

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

        Log.Information("Inbox wizard: sent '{Id}' to Claude in {Project}", msg.Id, _projectName);

        // Optimistic advance: Claude will mark done via MCP when it finishes.
        // The message stays in the list for now (user might want to re-read it),
        // but we move the wizard forward — match Mark done / Delete behaviour
        // for consistency.
        AdvanceAfterAction(removeCurrent: false);
    }

    private void OnMarkDoneClick(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        var msg = _messages[_index];

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
            Log.Information("Inbox wizard: marked '{Id}' done in {Project}", msg.Id, _projectName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Inbox wizard: mark-done failed for {Id}", msg.Id);
            MessageDialog.Show(this,
                title: "Could not mark as done",
                message: ex.Message,
                primaryLabel: "OK");
            return;
        }

        AdvanceAfterAction(removeCurrent: true);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        var msg = _messages[_index];

        var confirmed = MessageDialog.Show(this,
            title: "Delete this message?",
            message: $"Permanently delete \"{msg.Subject ?? "(no subject)"}\" from {_projectName}'s inbox?",
            primaryLabel: "Delete",
            secondaryLabel: "Cancel");
        if (!confirmed) return;

        try
        {
            File.Delete(msg.FullPath);
            Log.Information("Inbox wizard: deleted '{Id}' from {Project}", msg.Id, _projectName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Inbox wizard: delete failed for {Id}", msg.Id);
            MessageDialog.Show(this,
                title: "Could not delete",
                message: ex.Message,
                primaryLabel: "OK");
            return;
        }

        AdvanceAfterAction(removeCurrent: true);
    }

    private void AdvanceAfterAction(bool removeCurrent)
    {
        if (removeCurrent)
        {
            _messages.RemoveAt(_index);
            // Stay on the same index — that's now the "next" message. If we
            // were at the end, step back so the user lands on the new last.
            if (_index >= _messages.Count) _index = _messages.Count - 1;
        }
        else
        {
            if (_index < _messages.Count - 1) _index++;
        }

        if (_messages.Count == 0)
        {
            Close();
            return;
        }
        RenderCurrent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Keyboard shortcuts for quick triage. Esc closes (cancel-button on
        // none of the buttons keeps that working through WPF's defaults).
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                if (PrevButton.IsEnabled) { OnPrevClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Key.Right:
            case Key.PageDown:
                if (NextButton.IsEnabled) { OnNextClick(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
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
