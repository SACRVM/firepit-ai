using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Firepit.Core.Artifacts;
using Firepit.Web;
using Serilog;

namespace Firepit.Views;

/// <summary>
/// Shows one artifact. A window, deliberately, and not a pane inside the tab:
/// the tab belongs to the terminal, and putting a document in it is the
/// sub-tab cockpit that V1 was kept clear of.
/// </summary>
/// <remarks>
/// <para>
/// Only the three kinds Firepit can render itself get here — markdown, plain
/// text, images. A PDF or an installer has a real handler on the machine, and
/// clicking one still goes straight to it. This exists because markdown does
/// not: Windows has no association for <c>.md</c>, so the click that should
/// show a report produced an "Open with" dialog instead.
/// </para>
/// <para>
/// Not an editor and not a file browser. It renders what it was pointed at,
/// and the buttons at the bottom copy its text or hand the file to something
/// that can do more.
/// </para>
/// <para>
/// One at a time, and it belongs to the tab it was opened from. A second click
/// in the artifact pane shows that artifact in the window already open rather
/// than stacking another one on top, and switching tabs closes it — the pane
/// it came from has just changed to another project's artifacts.
/// </para>
/// </remarks>
public partial class ArtifactViewerWindow : Window
{
    private const string CopyLabel = "Copy all";
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.5);

    /// <summary>The open viewer, if any. UI thread only.</summary>
    private static ArtifactViewerWindow? _current;

    private readonly ArtifactPreviewView _view = new();
    private readonly DispatcherTimer _copyFeedback;
    private Task? _initialized;
    private string _absolutePath = string.Empty;
    private string? _text;
    private int _renderGeneration;

    private ArtifactViewerWindow()
    {
        InitializeComponent();

        // The caption row is laid out at 32px, but the close button sizes
        // itself from DialogCaptionPixelHeight, which scales with the UI font.
        // Every other window in Firepit reconciles the two here; this one did
        // not, so above the default font size the button was taller than the
        // row it sits in and the X landed off-centre. The chrome's caption
        // height goes with it, or the draggable strip stops matching the bar.
        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            {
                chrome.CaptionHeight = capH;
            }
        }

        ViewerHost.Child = _view.Element;

        _copyFeedback = new DispatcherTimer { Interval = CopiedFeedback };
        _copyFeedback.Tick += (_, _) =>
        {
            _copyFeedback.Stop();
            CopyButton.Content = CopyLabel;
        };

        Closed += (_, _) =>
        {
            _copyFeedback.Stop();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
            _view.Dispose();
        };
    }

    /// <summary>Close the open viewer, if there is one.</summary>
    public static void CloseCurrent() => _current?.Close();

    /// <summary>
    /// Open a viewer for <paramref name="artifact"/>, or return false if this
    /// is not a kind Firepit renders — the caller then falls back to the shell.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing on a read failure too. A file that
    /// vanished or is locked by the process that wrote it is an ordinary event
    /// here; the shell's own error is more useful to the user than ours.
    /// </remarks>
    public static bool TryShow(Window owner, ResolvedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!ArtifactPreview.CanPreview(artifact.Kind))
        {
            return false;
        }

        string? text = null;
        if (artifact.Kind is ArtifactKind.Markdown or ArtifactKind.Text)
        {
            try
            {
                text = File.ReadAllText(artifact.AbsolutePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read artifact {Path} for preview", artifact.AbsolutePath);
                return false;
            }
        }

        var directory = Path.GetDirectoryName(artifact.AbsolutePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        // Reused, not replaced: the window keeps the size and place the user
        // gave it, and the WebView2 behind it is already warm.
        var window = _current;
        if (window is null)
        {
            window = new ArtifactViewerWindow { Owner = owner };
            DialogSizing.ClampToScreen(window);
            window.Show();
            _current = window;
        }
        else
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
        }

        window.Load(artifact, text, directory);
        return true;
    }

    private void Load(ResolvedArtifact artifact, string? text, string directory)
    {
        _absolutePath = artifact.AbsolutePath;
        _text = text;
        CaptionText.Text = artifact.Label;
        Title = artifact.Label;

        // Text and markdown only: an image has no text to put on the
        // clipboard, and a button that copies nothing is a lie.
        CopyButton.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        _copyFeedback.Stop();
        CopyButton.Content = CopyLabel;

        // After Show: the WebView2 needs a live window to attach its hwnd to,
        // and the render is awaited on the UI thread so a failure lands in the
        // window the user is already looking at rather than nowhere.
        _ = RenderAsync(
            ArtifactPreview.Build(artifact.Kind, Path.GetFileName(artifact.AbsolutePath), text),
            directory);
    }

    private async Task RenderAsync(string html, string directory)
    {
        var generation = ++_renderGeneration;
        try
        {
            // Once per window: initialising a WebView2 a second time registers
            // its handlers and host mapping twice.
            _initialized ??= _view.InitializeAsync(CancellationToken.None);
            await _initialized;

            // A second artifact clicked while the first was still starting up
            // has the last word.
            if (generation != _renderGeneration)
            {
                return;
            }
            await _view.ShowAsync(html, directory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Artifact viewer failed for {Path}", _absolutePath);
            CaptionText.Text = $"{Title} — could not be displayed";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The file's text as it was read — for markdown that is the source, not
    /// the rendered page, which is what pasting into an agent or an editor
    /// wants.
    /// </summary>
    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(_text);
            CopyButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            // Another process holding the clipboard open is the usual cause,
            // and it is transient. Saying so beats a button that silently did
            // nothing.
            Log.Warning(ex, "Could not copy artifact text to the clipboard");
            CopyButton.Content = "Copy failed";
        }
        _copyFeedback.Stop();
        _copyFeedback.Start();
    }

    private void OnOpenExternallyClick(object sender, RoutedEventArgs e) =>
        Launch(new ProcessStartInfo(_absolutePath) { UseShellExecute = true }, "open");

    private void OnRevealClick(object sender, RoutedEventArgs e) =>
        Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{_absolutePath}\""), "reveal");

    private static void Launch(ProcessStartInfo start, string what)
    {
        try
        {
            // Fully qualified: bare `Process` binds to the Firepit.Process
            // namespace from inside namespace Firepit.Views.
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not {What} artifact from the viewer", what);
        }
    }
}
