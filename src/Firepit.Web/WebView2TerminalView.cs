using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Firepit.Core.Settings;
using Firepit.Core.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace Firepit.Web;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WebView2TerminalView : ITerminalView
{
    private static readonly Uri TerminalUri = new("https://firepit.local/terminal.html");

    // Serialize WebView2 + xterm.js boot across all tabs. Parallel inits during
    // cold-start with 4 tabs blew past the 15s ready-handshake timeout under
    // load: the tab survived as a zombie — ready arrived 30s+ late, session
    // was already Dead, no PTY channel, every keypress silently dropped.
    private static readonly SemaphoreSlim InitGate = new(1, 1);

    private readonly WebView2 _webView = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalThemeSettings _theme;
    private readonly int _fontSize;
    private bool _initialized;
    private bool _disposed;

    // Native OLE drop target registered on the WebView2 host HWND *and* every
    // descendant HWND. Kept alive by this field so the CCW isn't collected
    // while OLE holds it; the HWNDs are remembered separately so Dispose can
    // RevokeDragDrop each one even after teardown.
    private FileDropTarget? _fileDropTarget;
    private readonly List<IntPtr> _dropTargetHwnds = new();

    // Coalescing buffer for PTY output (see WriteAsync). A busy agent streams
    // many small chunks; batching them into one Background-priority dispatcher
    // flush keeps a single chatty tab from starving the UI thread shared by
    // every other tab.
    private readonly object _writeGate = new();
    private readonly MemoryStream _pendingWrites = new();
    private bool _flushScheduled;

    public WebView2TerminalView(TerminalThemeSettings? theme = null, int fontSize = 14)
    {
        _theme = (theme ?? TerminalThemeSettings.Defaults).Resolved();
        _fontSize = fontSize;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? InputReceived;
    public event EventHandler<TerminalSize>? Resized;
    public event EventHandler<bool>? ProgressChanged;

    public WebView2 Element => _webView;

    public TerminalSize? CurrentSize { get; private set; }

    public bool IsInitialized => _initialized;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        Log.Information("WV2 init: extracting assets");
        var assetsDir = WebAssetExtractor.Extract();
        Log.Information("WV2 init: assets at {Dir}", assetsDir);

        Log.Information("WV2 init: getting environment");
        var environment = await FirepitWebViewEnvironment.GetAsync().ConfigureAwait(true);
        Log.Information("WV2 init: environment ready (browser version {Version})", environment.BrowserVersionString);

        Log.Information("WV2 init: waiting for init gate (queued={Queued})", InitGate.CurrentCount == 0);
        await InitGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            Log.Information("WV2 init: ensuring CoreWebView2");
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            Log.Information("WV2 init: CoreWebView2 ready");

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                hostName: "firepit.local",
                folderPath: assetsDir,
                accessKind: CoreWebView2HostResourceAccessKind.Allow);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true; // V1 diagnostic — flip back later
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Turn off the browser layer's own drag-and-drop: it would
            // navigate to dropped files (Edge opens images in a preview) and
            // it registers a Chromium drop target on the inner HWNDs. With it
            // off, Firepit registers its own native IDropTarget on the host
            // HWND once init finishes — see RegisterFileDropTarget(). WPF's
            // managed drag-drop events can't be used: the WebView2 is an
            // HwndHost and its airspace is invisible to WPF hit-testing.
            _webView.AllowExternalDrop = false;

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
                Log.Information("WV2 NavigationCompleted: success={IsSuccess}, status={Status}", args.IsSuccess, args.WebErrorStatus);
            _webView.CoreWebView2.ProcessFailed += (_, args) =>
                Log.Error("WV2 ProcessFailed: kind={Kind}, reason={Reason}, exitCode={Exit}",
                    args.ProcessFailedKind, args.Reason, args.ExitCode);

            Log.Information("WV2 init: navigating to {Uri}", TerminalUri);
            _webView.Source = TerminalUri;

            Log.Information("WV2 init: waiting for ready handshake (15 s timeout)");
            try
            {
                await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
                Log.Information("WV2 init: ready received");
            }
            catch (TimeoutException)
            {
                Log.Error("WV2 init: ready handshake timed out after 15 s");
                throw new InvalidOperationException(
                    "Terminal renderer never reported ready. The WebView2 page may have failed to load — check the logs for NavigationCompleted status.");
            }
            ApplyTheme();
            ApplyFontSize();
            RegisterFileDropTarget();
            _initialized = true;
        }
        finally
        {
            InitGate.Release();
        }
    }

    private void ApplyFontSize()
    {
        var payload = $"{{\"type\":\"font\",\"size\":{_fontSize}}}";
        _webView.CoreWebView2.PostWebMessageAsString(payload);
    }

    private void ApplyTheme()
    {
        // Settings-driven palette. Sent after the ready handshake so xterm has
        // already constructed with the inline defaults — this just overrides.
        var payload = JsonSerializer.Serialize(new
        {
            type  = "theme",
            theme = new
            {
                background                  = _theme.Background,
                foreground                  = _theme.Foreground,
                cursor                      = _theme.Cursor,
                selectionBackground         = _theme.SelectionBackground,
                selectionForeground         = _theme.SelectionForeground,
                selectionInactiveBackground = _theme.SelectionInactiveBackground,
            }
        });
        _webView.CoreWebView2.PostWebMessageAsString(payload);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        // Coalesce: append to a shared buffer and schedule AT MOST ONE flush.
        // The old path marshalled every PTY chunk individually, and at the
        // default Normal priority — which in WPF outranks Input and Render. A
        // busy agent streaming hundreds of small chunks/sec therefore flooded
        // the UI thread with high-priority work, starving keyboard input,
        // rendering, and every other tab. Batching collapses a burst into a
        // single Background-priority post so user interaction always wins, and
        // xterm decodes one combined write instead of hundreds of tiny ones.
        bool schedule;
        lock (_writeGate)
        {
            // Dispose may have flipped _disposed (and is now waiting on this
            // same lock to dispose the buffer) — bail rather than write into a
            // stream that's about to go away.
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _pendingWrites.Write(data.Span);
            schedule = !_flushScheduled;
            _flushScheduled = true;
        }
        if (schedule)
        {
            _webView.Dispatcher.InvokeAsync(
                FlushPendingWrites,
                System.Windows.Threading.DispatcherPriority.Background);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drain the coalescing buffer and post it to xterm as one message. Runs on
    /// the UI thread at Background priority, so a flood of terminal output never
    /// preempts input or rendering.
    /// </summary>
    private void FlushPendingWrites()
    {
        byte[] buffer;
        lock (_writeGate)
        {
            _flushScheduled = false;
            if (_pendingWrites.Length == 0)
            {
                return;
            }
            buffer = _pendingWrites.ToArray();
            _pendingWrites.SetLength(0);
        }

        if (_disposed || _webView.CoreWebView2 is not { } core)
        {
            return;
        }
        var b64 = Convert.ToBase64String(buffer);
        core.PostWebMessageAsString($"{{\"type\":\"data\",\"b64\":\"{b64}\"}}");
    }

    public void Focus()
    {
        // Two-stage focus: WPF focuses the WebView2 host, then a bridge
        // message tells xterm.js to focus its hidden textarea. Without the
        // second stage, the WebView2 has focus but keystrokes go to the
        // browser layer rather than the terminal.
        if (_webView.Dispatcher.CheckAccess())
        {
            FocusCore();
        }
        else
        {
            _webView.Dispatcher.InvokeAsync(FocusCore);
        }
    }

    private void FocusCore()
    {
        try { _webView.Focus(); } catch { /* ignored */ }

        // Stage two is the one that matters. Stage one hands WPF focus to the
        // WebView2 host, so the keyboard now belongs to a control that drops
        // every keystroke until xterm's hidden textarea is focused. Losing
        // this message therefore doesn't just skip the focus — it makes the
        // user type into a dead terminal, with no visible cause. If the bridge
        // isn't up yet, wait for it rather than relying on the user to click.
        if (_webView.CoreWebView2 is null || !_readyTcs.Task.IsCompleted)
        {
            _ = FocusWhenBridgeReadyAsync();
            return;
        }
        try { _webView.CoreWebView2.PostWebMessageAsString("{\"type\":\"focus\"}"); }
        catch (Exception ex) { Log.Debug(ex, "WV2 focus message failed"); }
    }

    private async Task FocusWhenBridgeReadyAsync()
    {
        try
        {
            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Never became ready (init failed or the view was torn down).
            // Nothing useful left to do — a user click still refocuses.
            return;
        }
        if (_disposed) return;
        try { _webView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"focus\"}"); }
        catch (Exception ex) { Log.Debug(ex, "WV2 deferred focus message failed"); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_dropTargetHwnds.Count > 0)
        {
            foreach (var hwnd in _dropTargetHwnds)
            {
                try { NativeDragDrop.RevokeDragDrop(hwnd); }
                catch (Exception ex) { Log.Debug(ex, "WV2 drop: RevokeDragDrop failed for 0x{Hwnd:X}", hwnd.ToInt64()); }
            }
            _dropTargetHwnds.Clear();
            _fileDropTarget = null;
        }

        try
        {
            if (_webView.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
            }
        }
        catch { /* ignored */ }

        lock (_writeGate)
        {
            _pendingWrites.Dispose();
            _flushScheduled = false;
        }

        _webView.Dispose();
    }

    private void HandlePasteRequest()
    {
        string text;
        try
        {
            // Clipboard requires STA — WebView2 events fire on the WPF UI
            // thread (STA), so a direct call is fine. Fail closed if anything
            // throws (clipboard is occasionally locked by another process).
            text = System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WV2 paste-request: clipboard read failed");
            return;
        }
        // DIAGNOSTIC: one right-click must produce exactly ONE paste-request.
        // If a repro still shows a double paste, this line reveals whether
        // Firepit fired the paste twice (two log entries) or the app doubled it
        // (a single entry — clipboard delivered once by us).
        Log.Information("WV2 paste-request: delivering {Chars} chars to terminal", text.Length);
        PasteText(text);
    }

    private void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text) || _disposed) return;
        var payload = $"{{\"type\":\"paste\",\"text\":{JsonSerializer.Serialize(text)}}}";
        try { _webView.CoreWebView2.PostWebMessageAsString(payload); }
        catch (Exception ex) { Log.Debug(ex, "WV2 paste-text: post failed"); }
    }

    /// <summary>
    /// Register a native OLE drop target on the WebView2 host HWND and on every
    /// descendant window. WPF's managed drag-drop never fires over the HwndHost
    /// airspace, so a native <c>IDropTarget</c> is the only route. Registering
    /// on the host HWND alone is not enough: OLE looks up the drop target on the
    /// exact window under the cursor and does <em>not</em> walk up the parent
    /// chain, and the cursor is always over one of Chromium's render-widget
    /// child HWNDs that sit on top of the host. With <c>AllowExternalDrop=false</c>
    /// those children either have no target (→ "no-drop" cursor) or a rejecting
    /// one — so we register ours on each, revoking any existing target first.
    /// </summary>
    private void RegisterFileDropTarget()
    {
        var host = _webView.Handle;
        if (host == IntPtr.Zero)
        {
            Log.Warning("WV2 drop: host HWND unavailable — file drop disabled");
            return;
        }

        _fileDropTarget = new FileDropTarget(OnFilesDropped);

        var targets = new List<IntPtr> { host };
        NativeDragDrop.EnumChildWindows(host, (child, _) => { targets.Add(child); return true; }, IntPtr.Zero);

        foreach (var hwnd in targets)
        {
            var hr = NativeDragDrop.RegisterDragDrop(hwnd, _fileDropTarget);
            if (hr == NativeDragDrop.DragDropEAlreadyRegistered)
            {
                // Chromium owns a target on this child — replace it with ours.
                NativeDragDrop.RevokeDragDrop(hwnd);
                hr = NativeDragDrop.RegisterDragDrop(hwnd, _fileDropTarget);
            }
            if (hr == 0)
            {
                _dropTargetHwnds.Add(hwnd);
            }
            else
            {
                Log.Debug("WV2 drop: RegisterDragDrop failed for 0x{Hwnd:X} (0x{Hr:X8})", hwnd.ToInt64(), hr);
            }
        }

        if (_dropTargetHwnds.Count == 0)
        {
            Log.Warning("WV2 drop: no HWND accepted a drop target — file drop disabled");
            _fileDropTarget = null;
            return;
        }
        Log.Information("WV2 drop: file drop target registered on {Count} HWND(s) (host 0x{Host:X})",
            _dropTargetHwnds.Count, host.ToInt64());
    }

    private void OnFilesDropped(IReadOnlyList<string> paths)
    {
        // OLE invokes the drop target on the thread that registered it — the
        // WPF UI thread — so posting to the WebView2 directly is safe.
        if (paths.Count == 0) return;
        var formatted = FormatDroppedPaths(paths);
        Log.Information("WV2 drop: {Count} path(s) pasted", paths.Count);
        PasteText(formatted);
    }

    /// <summary>
    /// Format file paths for paste into the terminal. One path → bare or
    /// double-quoted if it contains whitespace. Multiple paths → space-
    /// separated, each quoted individually if needed. Matches Windows
    /// Terminal's drag-drop convention.
    /// </summary>
    internal static string FormatDroppedPaths(IReadOnlyList<string> paths)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var p = paths[i];
            if (NeedsQuoting(p))
            {
                sb.Append('"').Append(p).Append('"');
            }
            else
            {
                sb.Append(p);
            }
        }
        return sb.ToString();
    }

    private static bool NeedsQuoting(string path)
    {
        foreach (var c in path)
        {
            if (char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    private static void HandleCopy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WV2 copy: clipboard write failed");
        }
    }

    /// <summary>
    /// Ctrl+Click on a URL the web-links addon detected in terminal output.
    /// The terminal renders agent-controlled text, so treat the URL as
    /// untrusted: only http/https ever reach the system browser — no file://,
    /// no javascript:, no custom app protocols. Opening happens via
    /// ShellExecute in the default browser; the WebView2 never navigates.
    /// </summary>
    private static void HandleLink(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Debug("WV2 link: rejected non-web URL ({Url})", url);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            Log.Information("WV2 link: opened {Url}", uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WV2 link: could not open {Url}", uri.AbsoluteUri);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                return;
            }
            var type = typeProp.GetString();
            switch (type)
            {
                case "ready":
                    Log.Information("WV2 bridge: 'ready' received");
                    _readyTcs.TrySetResult();
                    break;
                case "input":
                    if (doc.RootElement.TryGetProperty("b64", out var b64Prop)
                        && b64Prop.ValueKind == JsonValueKind.String)
                    {
                        var bytes = Convert.FromBase64String(b64Prop.GetString()!);
                        InputReceived?.Invoke(this, bytes);
                    }
                    break;
                case "resize":
                    if (doc.RootElement.TryGetProperty("cols", out var colsProp)
                        && doc.RootElement.TryGetProperty("rows", out var rowsProp)
                        && colsProp.TryGetInt32(out var cols)
                        && rowsProp.TryGetInt32(out var rows))
                    {
                        var size = new TerminalSize(cols, rows);
                        CurrentSize = size;
                        Resized?.Invoke(this, size);
                    }
                    break;
                case "progress":
                    // OSC 9;4 from the agent. State 0 = cleared (idle), any
                    // other value = active. We don't differentiate normal vs
                    // error vs indeterminate at this layer — the activity
                    // detector only needs the boolean.
                    if (doc.RootElement.TryGetProperty("state", out var stateProp)
                        && stateProp.TryGetInt32(out var progressState))
                    {
                        ProgressChanged?.Invoke(this, progressState != 0);
                    }
                    break;
                case "paste-request":
                    HandlePasteRequest();
                    break;
                case "copy":
                    if (doc.RootElement.TryGetProperty("text", out var textProp)
                        && textProp.ValueKind == JsonValueKind.String)
                    {
                        HandleCopy(textProp.GetString());
                    }
                    break;
                case "link":
                    if (doc.RootElement.TryGetProperty("url", out var urlProp)
                        && urlProp.ValueKind == JsonValueKind.String)
                    {
                        HandleLink(urlProp.GetString());
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // dropped — handler is robust against malformed messages
        }
        catch (FormatException)
        {
            // base64 decode failed — drop
        }
    }
}
