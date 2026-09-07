using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Firepit.Core.Artifacts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace Firepit.Web;

/// <summary>
/// The WebView2 half of the artifact viewer: takes a finished HTML document
/// and a directory to serve alongside it, and shows the result.
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than in the shell for the same reason the terminal does —
/// WebView2 types stay inside this project, and the window that hosts this only
/// ever sees a <see cref="FrameworkElement"/>.
/// </para>
/// <para>
/// Two deliberate restrictions. Scripting is off: the document is built from a
/// file on disk that Firepit did not write, markdown may legitimately contain
/// raw HTML, and a viewer has no reason to execute anything. And a link that
/// wants a new window gets the system browser instead of a second WebView2 —
/// the viewer shows one artifact, it is not a browser.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ArtifactPreviewView : IDisposable
{
    /// <summary>Host serving the document itself, kept apart from the
    /// artifact's own directory so writing it never touches the user's
    /// folder.</summary>
    private const string DocumentHost = "firepit.view";

    private readonly WebView2 _webView = new();
    private readonly string _documentDir;
    private readonly string _documentPath;
    private string? _mappedArtifactDir;
    private bool _disposed;

    public ArtifactPreviewView()
    {
        // One directory per view, so two open viewers never overwrite each
        // other's document. Under TEMP rather than LocalAppData on purpose:
        // Dispose removes it, but a crash cannot, and TEMP is the one place
        // the operating system already cleans up after us. Firepit's own
        // folder would collect a directory per viewer, for ever.
        _documentDir = Path.Combine(
            Path.GetTempPath(),
            "Firepit",
            "viewer",
            Guid.NewGuid().ToString("N"));
        _documentPath = Path.Combine(_documentDir, "view.html");
    }

    public FrameworkElement Element => _webView;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var environment = await FirepitWebViewEnvironment.GetAsync().ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var settings = _webView.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsZoomControlEnabled = true;

        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x15, 0x11, 0x0D);

        Directory.CreateDirectory(_documentDir);
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            DocumentHost, _documentDir, CoreWebView2HostResourceAccessKind.Allow);
    }

    /// <summary>
    /// Render <paramref name="html"/>, serving <paramref name="artifactDir"/>
    /// under <see cref="ArtifactPreview.VirtualHost"/> so images and relative
    /// links inside the document resolve.
    /// </summary>
    public async Task ShowAsync(string html, string artifactDir)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(artifactDir);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Remap only when the directory changes: SetVirtualHostNameToFolder-
        // Mapping throws if the same host is registered twice.
        if (!string.Equals(_mappedArtifactDir, artifactDir, StringComparison.OrdinalIgnoreCase))
        {
            if (_mappedArtifactDir is not null)
            {
                _webView.CoreWebView2.ClearVirtualHostNameToFolderMapping(ArtifactPreview.VirtualHost);
            }
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ArtifactPreview.VirtualHost, artifactDir, CoreWebView2HostResourceAccessKind.Allow);
            _mappedArtifactDir = artifactDir;
        }

        // Through a file rather than NavigateToString, which caps at 2 MB — a
        // pinned build log reaches that, and the failure would be a blank
        // window rather than an error.
        await File.WriteAllTextAsync(_documentPath, html).ConfigureAwait(true);
        _webView.CoreWebView2.Navigate($"https://{DocumentHost}/view.html?r={Guid.NewGuid():N}");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Uri} from the artifact viewer", e.Uri);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
            _webView.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Disposing the artifact viewer's WebView2 failed");
        }

        try
        {
            if (Directory.Exists(_documentDir))
            {
                Directory.Delete(_documentDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // A leftover temp document is harmless; failing to close the
            // window over it would not be.
            Log.Debug(ex, "Could not clean up the artifact viewer's temp directory");
        }
    }
}
