using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
/// and the two buttons at the bottom hand the file to something that can do
/// more.
/// </para>
/// </remarks>
public partial class ArtifactViewerWindow : Window
{
    private readonly ArtifactPreviewView _view = new();
    private readonly string _absolutePath;

    private ArtifactViewerWindow(ResolvedArtifact artifact)
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

        _absolutePath = artifact.AbsolutePath;
        CaptionText.Text = artifact.Label;
        Title = artifact.Label;
        ViewerHost.Child = _view.Element;
        Closed += (_, _) => _view.Dispose();
    }

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

        var window = new ArtifactViewerWindow(artifact) { Owner = owner };
        DialogSizing.ClampToScreen(window);
        window.Show();

        // After Show: the WebView2 needs a live window to attach its hwnd to,
        // and the render is awaited on the UI thread so a failure lands in the
        // window the user is already looking at rather than nowhere.
        _ = window.RenderAsync(
            ArtifactPreview.Build(artifact.Kind, Path.GetFileName(artifact.AbsolutePath), text),
            directory);
        return true;
    }

    private async Task RenderAsync(string html, string directory)
    {
        try
        {
            await _view.InitializeAsync(CancellationToken.None);
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
