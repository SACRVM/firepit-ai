using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Firepit.Core.Agents;
using Firepit.Core.Mcp;
using Firepit.Core.Process;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Projects;
using Firepit.Core.QuickLinks;
using Firepit.Core.Sessions;
using Firepit.Core.State;
using Firepit.Core.Settings;
using Firepit.Core.Terminal;
using Firepit.Core.Time;
using Firepit.Process;
using Firepit.Web;
using Serilog;

namespace Firepit.Views;

public sealed class SessionTab : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    // Matches WindowChrome.ResizeBorderThickness in MainWindow.xaml. The
    // WebView2 is a child HWND — WindowChrome's WM_NCHITTEST hook lives on the
    // top-level window and never fires for pixels a child HWND covers. So the
    // terminal is inset by the resize-border width on its three non-caption
    // edges, exposing a ring at the window edge where the chrome's resize
    // hit-testing actually works — including the diagonal bottom corners.
    private static readonly Thickness TerminalResizeInset = new(6, 0, 6, 6);

    private readonly IAgentAdapter _adapter;
    private readonly IQuickLinkService _quickLinks;
    private readonly IMcpRegistry? _mcpRegistry;
    private readonly IAgentMcpProjector? _mcpProjector;
    private readonly TerminalThemeSettings? _terminalTheme;
    private readonly int _terminalFontSize;
    private readonly ActivityDetector _detector;
    private readonly Grid _content;
    private readonly Grid _terminalArea;
    private readonly TextBlock _statusText;
    private readonly TextBlock _headerText;
    private readonly StackPanel _headerPanel;
    private readonly Border _inboxBadge;
    private readonly TextBlock _inboxBadgeCount;
    private readonly Border _runsBadge;
    private readonly TextBlock _runsBadgeCount;
    private readonly TabToolbar _toolbar;
    private readonly Border _rekindleAffordance;
    private readonly Border _configRestartAffordance;
    private readonly Border _mcpHealthAffordance;
    private readonly TextBlock _mcpHealthLabel;
    private readonly StackPanel _loadingIndicator;
    private readonly McpHealthChecker _mcpHealth = new();
    private Firepit.Core.ProjectConfig.ProjectConfig? _currentConfig;
    private readonly RotateTransform _spinnerRotate;
    private readonly Storyboard _spinnerStoryboard;
    private readonly DispatcherTimer _tickTimer;
    private WebView2TerminalView? _terminalView;
    private IPtyChannel? _ptyChannel;
    private CancellationTokenSource? _cts;
    private Task? _startTask;
    private readonly Firepit.Core.ProjectConfig.CommandsTrustLedger? _trustLedger;
    private readonly string _editorCommand;
    private readonly CommandRunner _commandRunner;
    private bool _initialized;
    private bool _disposed;

    public SessionTab(
        ProjectContext context,
        IAgentAdapter adapter,
        IQuickLinkService quickLinks,
        IMcpRegistry? mcpRegistry = null,
        IAgentMcpProjector? mcpProjector = null,
        IActivityClock? clock = null,
        TerminalThemeSettings? terminalTheme = null,
        int terminalFontSize = 14,
        Firepit.Core.ProjectConfig.ProjectConfig? initialConfig = null,
        Firepit.Core.ProjectConfig.CommandsTrustLedger? trustLedger = null,
        string editorCommand = "code")
    {
        _trustLedger = trustLedger;
        _editorCommand = editorCommand;
        Context = context;
        _adapter = adapter;
        _quickLinks = quickLinks;
        _mcpRegistry = mcpRegistry;
        _mcpProjector = mcpProjector;
        _terminalTheme = terminalTheme;
        _terminalFontSize = terminalFontSize;
        _currentConfig = initialConfig;

        _detector = new ActivityDetector(clock ?? new SystemActivityClock());
        _detector.StateChanged += OnStateChanged;

        // CommandRunner emits state-change callbacks from arbitrary threads
        // (Process.Exited fires on a worker). Marshal back to the UI dispatcher
        // before touching the toolbar so we don't cross thread-affinity.
        _commandRunner = new CommandRunner(() =>
        {
            if (_disposed) return;
            _content?.Dispatcher.BeginInvoke(new Action(RefreshCommandRunningState));
        });

        _statusText = new TextBlock
        {
            Text = $"Igniting {context.Name}…",
            Foreground = StateColors.Brush(SessionState.Igniting),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = ResolveFontSize("TitleFontSize", 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        (_loadingIndicator, _spinnerRotate, _spinnerStoryboard) = BuildLoadingIndicator(context.Name);

        _terminalArea = new Grid();
        // Loading indicator is mounted lazily by ShowLoadingIndicator() when
        // StartSessionAsync runs. A SessionTab can be created in deferred mode
        // — UI ready, session not yet started — and then activated later when
        // the user clicks it. Pre-mounting the spinner would visually claim
        // "this tab is loading" the moment it's constructed.

        _toolbar = new TabToolbar();
        _toolbar.RekindleRequested += (_, _) => _ = RekindleAsync(resume: false, confirmIfBurning: true);
        _toolbar.ResumeRequested += (_, _) => _ = RekindleAsync(resume: true, confirmIfBurning: true);
        _toolbar.ExplorerRequested += (_, _) => OpenExplorer();
        _toolbar.ShellRequested += (_, elevated) => OpenExternalShell(elevated);
        _toolbar.EditorRequested += (_, _) => OpenEditor();
        _toolbar.ConfigureRequested += (_, _) => OpenProjectConfig();
        // Owned by the shell: the dialog needs the meta project's path and has
        // to trigger a knowledge rescan, neither of which is a tab's business.
        _toolbar.ProjectSettingsRequested += (_, _) =>
            ProjectSettingsRequested?.Invoke(this, EventArgs.Empty);
        _toolbar.InboxRequested += (_, _) => OnInboxButtonClicked();
        _toolbar.QuickLinkClicked += (_, link) => OpenQuickLink(link);
        _toolbar.CommandClicked += (_, cmd) => RunCommand(cmd);
        _toolbar.CommandStopRequested += (_, cmd) => StopCommand(cmd);
        // After any toolbar action, push focus back to the terminal so typing
        // continues at the prompt instead of bouncing off a button that's
        // still focused. Routed-event delivery is synchronous, so by the time
        // this fires the action has already run (incl. any modal close).
        _toolbar.FocusReturnRequested += (_, _) => FocusTerminal();
        _toolbar.SetQuickLinks(_quickLinks.ResolveForProject(context));
        _toolbar.SetCommands(initialConfig?.Commands ?? [], _commandRunner.IsAlive);

        _rekindleAffordance = BuildRekindleAffordance();
        _configRestartAffordance = BuildConfigRestartAffordance();
        (_mcpHealthAffordance, _mcpHealthLabel) = BuildMcpHealthAffordance();

        _terminalArea.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x16, 0x12));

        _content = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x16, 0x12)),
        };
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_toolbar, 0);
        Grid.SetRow(_terminalArea, 1);
        _content.Children.Add(_toolbar);
        _content.Children.Add(_terminalArea);
        _content.Loaded += (_, _) => Log.Information(
            "SessionTab content loaded for {Project}: contentSize={W}x{H}, toolbarVisible={ToolbarVisible}",
            Context.Name, _content.ActualWidth, _content.ActualHeight, _toolbar.IsVisible);

        _headerText = new TextBlock
        {
            Text = context.Name,
            Foreground = StateColors.Brush(SessionState.Cold),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Inbox badge sits to the right of the project name. Hidden when
        // count is zero. Click handler is attached by MainWindow when it
        // wires the watcher (per-project knowledge of the inbox path lives
        // there, not here).
        _inboxBadgeCount = new TextBlock
        {
            Text = "0",
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x11, 0x0D)),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _inboxBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            ToolTip = "Inbox messages — click to open folder",
            Child = _inboxBadgeCount,
        };

        // Runs badge sits next to the inbox badge. Different colour so the two
        // are distinguishable at a glance — embers-warm for runs, igniting-warm
        // for inbox. Tooltip explains which is which. Hidden when count is zero.
        _runsBadgeCount = new TextBlock
        {
            Text = "0",
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x11, 0x0D)),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _runsBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xC9, 0x9A, 0x5C)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            ToolTip = "Scheduled job runs — click to open runs folder",
            Child = _runsBadgeCount,
        };

        _headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _headerPanel.Children.Add(_headerText);
        _headerPanel.Children.Add(_inboxBadge);
        _headerPanel.Children.Add(_runsBadge);

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TickInterval,
        };
        _tickTimer.Tick += (_, _) => _detector.Tick();
    }

    public ProjectContext Context { get; }

    /// <summary>The user asked for this project's settings dialog.</summary>
    public event EventHandler? ProjectSettingsRequested;

    public UIElement Content => _content;

    public UIElement Header => _headerPanel;

    /// <summary>
    /// Update the tab-header inbox notification badge — "new since last
    /// activated." Set newSinceSeenCount=0 to hide. The click handler is
    /// attached by MainWindow because it owns the inbox-folder path.
    /// </summary>
    public void SetInboxBadge(int newSinceSeenCount, MouseButtonEventHandler? onClick = null)
    {
        if (_disposed) return;
        if (newSinceSeenCount <= 0)
        {
            _inboxBadge.Visibility = Visibility.Collapsed;
            return;
        }
        _inboxBadgeCount.Text = newSinceSeenCount > 99 ? "99+" : newSinceSeenCount.ToString();
        _inboxBadge.Visibility = Visibility.Visible;
        if (onClick is not null)
        {
            _inboxBadge.MouseLeftButtonUp -= onClick;  // dedup before re-attach
            _inboxBadge.MouseLeftButtonUp += onClick;
        }
    }

    /// <summary>
    /// Update the always-visible Inbox toolbar button counter — total
    /// un-processed messages. Click handler is wired in the constructor
    /// (raises InboxRequested → <see cref="OnInboxButtonClicked"/>).
    /// </summary>
    public void SetInboxToolbarCount(int unprocessedCount)
    {
        if (_disposed) return;
        _unprocessedInboxCount = Math.Max(0, unprocessedCount);
        _toolbar.SetInboxCount(_unprocessedInboxCount);
    }

    private int _unprocessedInboxCount;

    /// <summary>
    /// Toolbar-button click handler: ask once, then hand the whole inbox to
    /// the running Claude session as a single prompt. Claude has the
    /// firepit_inbox_list / firepit_inbox_complete MCP tools; this just
    /// kicks off the workflow. The user can Ctrl-C in the terminal to abort
    /// at any point.
    /// </summary>
    private void OnInboxButtonClicked()
    {
        if (_disposed) return;
        if (_unprocessedInboxCount <= 0) return; // shouldn't happen — button is disabled

        var ownerWin = Window.GetWindow(_content);
        if (ownerWin is null) return;

        InboxWindow.Show(
            owner:       ownerWin,
            projectName: Context.Name,
            projectPath: Context.Path,
            sendToPty:   PasteIntoSession);

        // Hand focus back to the terminal once the wizard closes so the next
        // keystroke goes to Claude's input, not the still-focused toolbar button.
        FocusTerminal();
    }

    /// <summary>
    /// Long enough for an agent TUI to close its paste-coalescing window, short
    /// enough that the user never sees the gap. See <see cref="PasteIntoSession"/>.
    /// </summary>
    private static readonly TimeSpan PromptSubmitDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Shared "type this into the live PTY as if the user did" helper, used by
    /// the inbox actions. Delivers the prompt and submits it.
    /// </summary>
    private void PasteIntoSession(string prompt)
    {
        if (_ptyChannel is null)
        {
            var owner = Window.GetWindow(_content);
            MessageDialog.Show(owner,
                title: "Session not running",
                message: "Start or resume the session first — the prompt is delivered through Claude in this tab.",
                primaryLabel: "OK",
                secondaryLabel: null);
            return;
        }
        _ = SubmitPromptAsync(prompt, _cts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Deliver a prompt to the live session with no UI of its own. Returns
    /// false when there is no running session, so an automated caller can
    /// leave the work queued instead of popping a dialog the user never asked
    /// for. <see cref="PasteIntoSession"/> is the interactive counterpart.
    /// </summary>
    public bool TryDeliverPrompt(string prompt)
    {
        if (_disposed || _ptyChannel is null) return false;
        _ = SubmitPromptAsync(prompt, _cts?.Token ?? CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Write the prompt, then send the carriage return on its own.
    ///
    /// The return used to ride along in the same write. Agent TUIs coalesce a
    /// burst that arrives in a single read into a *paste*, and a carriage
    /// return inside that burst counts as a newline in the pasted text rather
    /// than as submit — so the prompt just sat in the input box waiting for
    /// Enter, immediately after the user clicked the button whose whole job was
    /// to send it. Delivered separately, once the paste window has closed, it
    /// registers as a keystroke again.
    /// </summary>
    private async Task SubmitPromptAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var channel = _ptyChannel;
            if (channel is null) return;
            await channel.WriteAsync(System.Text.Encoding.UTF8.GetBytes(prompt), ct);
            await Task.Delay(PromptSubmitDelay, ct);
            if (_disposed) return;
            // Re-read: the session can be rekindled or torn down inside the delay.
            var live = _ptyChannel;
            if (live is null) return;
            await live.WriteAsync("\r"u8.ToArray(), ct);
        }
        catch (OperationCanceledException) { /* session stopped mid-delivery */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "Prompt delivery failed for {Project}", Context.Name);
        }
    }

    /// <summary>
    /// Update the scheduled-job runs badge. Counts depend on the effective
    /// <see cref="RunBadgePolicy"/> wired up by MainWindow. Set unreadCount=0
    /// to hide.
    /// </summary>
    public void SetRunsBadge(int unreadCount, MouseButtonEventHandler? onClick = null)
    {
        if (_disposed) return;
        if (unreadCount <= 0)
        {
            _runsBadge.Visibility = Visibility.Collapsed;
            return;
        }
        _runsBadgeCount.Text = unreadCount > 99 ? "99+" : unreadCount.ToString();
        _runsBadge.Visibility = Visibility.Visible;
        if (onClick is not null)
        {
            _runsBadge.MouseLeftButtonUp -= onClick;  // dedup before re-attach
            _runsBadge.MouseLeftButtonUp += onClick;
        }
    }

    public SessionState State => _detector.State;

    /// <summary>
    /// True once <see cref="EnsureInitializedAsync"/> or
    /// <see cref="RekindleAsync"/> has been called for this tab. Deferred
    /// tabs (created on restore but not yet activated) return false; their
    /// PTY + WebView2 only spin up when the user actually clicks them.
    /// </summary>
    public bool IsStarted => _initialized;

    public Task EnsureInitializedAsync() => StartSessionAsync(resume: false);

    /// <summary>
    /// Deferred-mode entry point: identical to <see cref="EnsureInitializedAsync"/>
    /// but takes the resume flag from the persisted state. Used by the tab-
    /// selection handler when a restored tab is activated for the first time.
    /// </summary>
    public Task EnsureInitializedAsync(bool resume) => StartSessionAsync(resume);

    /// <summary>
    /// Pending resume flag for restored-but-not-yet-started tabs. Lives on
    /// SessionTab itself (rather than a sidecar dictionary on MainWindow) so
    /// it survives spurious cancellations and tab-list reshuffles —
    /// previously a phantom SelectionChanged race during restore could
    /// start-and-cancel a deferred tab once, consume its sidecar flag, and
    /// leave the user with a fresh (no <c>--continue</c>) session on the
    /// next real click. The starter clears this flag only after the session
    /// is actually started (see <see cref="RestartIfPending"/>).
    /// </summary>
    public bool PendingResume { get; set; }

    /// <summary>
    /// Idempotent "wake the deferred tab" entry point. Called by the
    /// tab-selection handler. If the session has never started, starts it
    /// honouring <see cref="PendingResume"/>. If a previous start cancelled
    /// (state is Dead from the cancel-catch's NotifyExited), re-attempts
    /// the same resume flag — this is what makes clicking a restored tab
    /// reliably get <c>--continue</c> even after a phantom cancel.
    /// </summary>
    public async Task RestartIfPending()
    {
        if (_disposed) return;

        // A boot is already underway — let it finish. Interrupting it here
        // (teardown + fresh start) was exactly what killed sessions during
        // rapid tab-switching: each re-select aborted the in-flight xterm
        // "ready" handshake, the WebView2 timed out, and the tab went Dead.
        if (_startTask is { IsCompleted: false })
        {
            return;
        }

        var needsStart = !_initialized
                          || _detector.State == SessionState.Dead
                          || _detector.State == SessionState.Cold;
        if (!needsStart) return;

        // A session that already ran (and is now Dead because the agent
        // process exited) MUST be brought back with --continue, never fresh:
        // restarting it blank silently throws away the whole conversation, so
        // reactivating such a tab looked like "the session forgot everything /
        // is stuck in an old session". Only a never-initialised (deferred-
        // restore) tab honours its persisted PendingResume flag.
        var wasInitialized = _initialized;
        var resume = PendingResume || wasInitialized;
        // DIAGNOSTIC (v0.5.38): a restart is actually firing — log who/why so
        // repeated respawns are traceable to their trigger in the log.
        Log.Information("RestartIfPending firing for {Project}: state={State}, wasInitialized={Init}, resume={Resume}",
            Context.Name, _detector.State, wasInitialized, resume);
        if (wasInitialized)
        {
            // Dead/Cold state from a prior run (or a cancelled attempt) — tear
            // down the half-built bits before retrying so StartSessionAsync's
            // early-return guard doesn't keep us stuck.
            await TeardownSessionAsync();
        }
        await StartSessionAsync(resume);

        // Only clear after the start actually got past _cts creation. If
        // StartSessionAsync immediately threw, PendingResume stays set and
        // the next click retries.
        if (_initialized)
        {
            PendingResume = false;
        }
    }

    /// <summary>
    /// Hand focus to the embedded terminal so the user can type immediately
    /// (no click needed). Safe to call before WebView2 is ready — the inner
    /// xterm focus message is best-effort and silently no-ops if the bridge
    /// isn't up yet.
    /// </summary>
    public void FocusTerminal() => _terminalView?.Focus();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _tickTimer.Stop();
        StopSpinner();

        try { _cts?.Cancel(); } catch { /* ignored */ }

        if (_ptyChannel is not null)
        {
            try { await _ptyChannel.DisposeAsync(); } catch { /* ignored */ }
            _ptyChannel = null;
        }
        if (_terminalView is not null)
        {
            try { _terminalView.Dispose(); } catch { /* ignored */ }
            _terminalView = null;
        }

        _detector.StateChanged -= OnStateChanged;
        _cts?.Dispose();
        try { _commandRunner.Dispose(); } catch { /* ignored */ }
    }

    public async Task RekindleAsync(bool resume, bool confirmIfBurning)
    {
        if (_disposed)
        {
            return;
        }

        if (confirmIfBurning && _detector.State == SessionState.Burning)
        {
            var owner = Window.GetWindow(_content);
            var confirmed = MessageDialog.Show(
                owner,
                title: "Restart session?",
                message: $"The session in {Context.Name} is still burning. Restarting will kill the running agent and start a fresh one.",
                primaryLabel: "Restart",
                secondaryLabel: "Cancel");
            if (!confirmed)
            {
                return;
            }
        }

        await TeardownSessionAsync();
        await StartSessionAsync(resume);
    }

    /// <summary>
    /// Idempotent session-boot entry point. Multiple callers (deferred-tab
    /// wake, EnsureInitialized, restore kick) can race here during rapid tab
    /// switching; this coalesces them onto a single in-flight boot so the
    /// WebView2 + xterm "ready" handshake is never interrupted mid-flight.
    /// </summary>
    private Task StartSessionAsync(bool resume)
    {
        if (_startTask is { IsCompleted: false })
        {
            return _startTask;
        }
        if (_initialized && _detector.State != SessionState.Dead && _detector.State != SessionState.Cold)
        {
            return Task.CompletedTask;
        }
        _startTask = StartSessionCoreAsync(resume);
        return _startTask;
    }

    private async Task StartSessionCoreAsync(bool resume)
    {
        _initialized = true;

        // Hoisted out of the try so the catch filter below can still tell a
        // torn-down-mid-boot abort from a genuine start failure — TeardownSession
        // nulls the _cts field, but this local stays valid.
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var ct = cts.Token;

            _detector.NotifyIgniting();
            _tickTimer.Start();
            ShowLoadingIndicator();

            if (_terminalView is null)
            {
                Log.Debug("Creating WebView2TerminalView for {Project}", Context.Name);
                _terminalView = new WebView2TerminalView(_terminalTheme, _terminalFontSize);
                // Hidden until first PTY-output. WebView2 is a HwndHost — its
                // native window always renders ON TOP of any sibling WPF
                // element regardless of ZIndex (the airspace rule). Without
                // hiding it, the spinner is technically present but invisible
                // because the WebView2 hwnd paints over it.
                _terminalView.Element.Visibility = Visibility.Hidden;
                _terminalView.Element.Margin = TerminalResizeInset;
                _terminalArea.Children.Add(_terminalView.Element);
                await _terminalView.InitializeAsync(ct);
                _terminalView.InputReceived += OnInputReceived;
                _terminalView.Resized += OnResized;
                _terminalView.ProgressChanged += OnProgressChanged;
                Log.Debug("WebView2 ready for {Project}", Context.Name);
            }

            if (_terminalArea.Children.Contains(_statusText))
            {
                _terminalArea.Children.Remove(_statusText);
            }
            // The spinner stays visible until the agent's first byte actually paints —
            // WebView2 mounting is not the same as Claude Code being ready.
            HideRekindleAffordance();

            await ApplyMcpAsync(ct);

            var spec = _adapter.BuildLaunchSpec(Context, new AgentLaunchOptions(Resume: resume));
            var initialSize = _terminalView!.CurrentSize ?? TerminalSize.Default;
            Log.Information("Spawning agent for {Project}: {Executable} {Args} in {Cwd} (size {Cols}x{Rows})",
                Context.Name, spec.Executable, string.Join(' ', spec.Arguments), spec.WorkingDirectory,
                initialSize.Cols, initialSize.Rows);

            // Inject FIREPIT_PROJECT_NAME so the agent (and any tools / MCP
            // bridges it spawns) know which Firepit project they're in. The
            // firepit-mcp.exe bridge reads this on connect to populate the
            // 'from' field of cross-Claude inbox messages.
            var env = new Dictionary<string, string?>(spec.EnvironmentOverrides ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
            {
                ["FIREPIT_PROJECT_NAME"] = _currentConfig?.Id ?? Context.Name,
            };

            _ptyChannel = await ConPtyLauncher.SpawnAsync(
                executable: spec.Executable,
                arguments: spec.Arguments,
                workingDirectory: spec.WorkingDirectory,
                environmentOverrides: env,
                initialSize: initialSize,
                ct: ct);
            Log.Information("Agent spawned for {Project}: pid={Pid}", Context.Name, _ptyChannel.Pid);

            // Apply any size update that arrived between init and now (e.g., user resized the
            // window during WebView2 startup).
            if (_terminalView.CurrentSize is { } latest
                && (latest.Cols != initialSize.Cols || latest.Rows != initialSize.Rows))
            {
                _ptyChannel.Resize(latest.Cols, latest.Rows);
            }

            _ = PumpOutputAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Session start cancelled for {Project}", Context.Name);
            // Without this reset the tab gets stuck in Igniting forever: the
            // next StartSessionAsync early-returns (because _initialized is
            // true and state isn't Dead/Cold), and clicking the tab again
            // does nothing. NotifyExited transitions to Dead so a retry
            // through RestartIfPending or Rekindle can take.
            _initialized = false;
            _detector.NotifyExited();
        }
        catch (Exception ex) when (cts.IsCancellationRequested || _disposed)
        {
            // A restart / teardown that lands while WebView2 is still creating
            // its controller ABORTS that call. WebView2 surfaces the abort as a
            // COMException whose message reads "Class not registered" — but the
            // HR is 0x80004004 (E_ABORT), not REGDB_E_CLASSNOTREG (0x80040154).
            // We used to splash it across the tab as a red fatal even though the
            // retry that immediately follows succeeds. It's a cancellation.
            Log.Information("Session start aborted mid-init for {Project}: {Message}",
                Context.Name, ex.Message);
            _initialized = false;
            _detector.NotifyExited();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Session start failed for {Project}", Context.Name);
            // Reset like the cancel path: leaving _initialized=true would make
            // the next StartSessionAsync early-return, so a click on the
            // "session went out" affordance (→ Rekindle) would be the only way
            // back. Clearing it lets RestartIfPending retry cleanly.
            _initialized = false;
            ShowFatal(ex.Message);
            _detector.NotifyExited();
        }
    }

    private async Task ApplyMcpAsync(CancellationToken ct)
    {
        if (_mcpRegistry is null || _mcpProjector is null)
        {
            return;
        }
        var active = _mcpRegistry.ResolveForProject(Context);
        Log.Information("Projecting {Count} MCP server(s) for {Project}", active.Count, Context.Name);
        foreach (var server in active)
        {
            foreach (var warning in server.ResolutionWarnings)
            {
                Log.Warning("MCP {Server} resolution: {Warning}", server.Id, warning);
            }
        }
        await _mcpProjector.ApplyAsync(Context, active, ct).ConfigureAwait(true);

        var issues = _mcpHealth.Check(active);
        foreach (var issue in issues)
        {
            Log.Warning("MCP health issue for {Project}: {Server} — {Detail}",
                Context.Name, issue.ServerId, issue.Detail);
        }
        ShowMcpHealthIssues(issues);
    }

    private async Task TeardownSessionAsync()
    {
        // DIAGNOSTIC (v0.5.38): Firepit is about to kill the current agent
        // process. Pairs with the pump's exit log to show whether a Dead
        // session was Firepit-initiated or the agent leaving on its own.
        Log.Information("Tearing down session for {Project} (state {State}, pid {Pid})",
            Context.Name, _detector.State, _ptyChannel?.Pid);
        try { _cts?.Cancel(); } catch { /* ignored */ }
        if (_ptyChannel is not null)
        {
            try { await _ptyChannel.DisposeAsync(); } catch { /* ignored */ }
            _ptyChannel = null;
        }
        // If WV2 init was cancelled mid-flight (typical when the user mashes
        // Restart while the first session is still booting), the view is
        // half-built — CoreWebView2 may be null, every WriteAsync would NRE.
        // Reusing it would leave the console blank ("frozen"). Drop it so the
        // next StartSessionAsync creates a fresh view from scratch.
        if (_terminalView is not null && !_terminalView.IsInitialized)
        {
            Log.Information("Dropping half-initialised terminal view for {Project}", Context.Name);
            try
            {
                if (_terminalArea.Children.Contains(_terminalView.Element))
                {
                    _terminalArea.Children.Remove(_terminalView.Element);
                }
                _terminalView.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Disposing half-initialised terminal failed for {Project}", Context.Name);
            }
            _terminalView = null;
        }
        _cts?.Dispose();
        _cts = null;
        _startTask = null;
        _initialized = false;
    }

    private async Task PumpOutputAsync(CancellationToken ct)
    {
        if (_ptyChannel is null || _terminalView is null)
        {
            return;
        }

        var ch = _ptyChannel;
        var pid = ch.Pid;
        var faulted = false;
        try
        {
            await foreach (var chunk in ch.ReadAsync(ct))
            {
                _detector.NotifyRead();
                await _terminalView.WriteAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            faulted = true;
            Log.Warning(ex, "Output pump for {Project} (pid {Pid}) faulted", Context.Name, pid);
            await _content.Dispatcher.InvokeAsync(() => ShowFatal(ex.Message));
        }
        finally
        {
            if (!_disposed)
            {
                // DIAGNOSTIC (v0.5.38): distinguish an agent that exited on its
                // own (PTY EOF, no cancellation) from a Firepit-initiated
                // teardown. The former is the real "session keeps dying"
                // mystery — log its exit code so we can see WHY claude leaves.
                if (ct.IsCancellationRequested)
                {
                    Log.Information("Output pump for {Project} (pid {Pid}) stopped by teardown", Context.Name, pid);
                }
                else if (!faulted)
                {
                    int? code = null;
                    try { code = await ch.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch { /* exit code unavailable */ }
                    Log.Warning("Agent for {Project} (pid {Pid}) EXITED ON ITS OWN (code {Code}) — session going Dead",
                        Context.Name, pid, code);
                }
                _detector.NotifyExited();
            }
        }
    }

    private async void OnInputReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_ptyChannel is null || _cts is null)
        {
            return;
        }
        try
        {
            await _ptyChannel.WriteAsync(data, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnResized(object? sender, TerminalSize size)
    {
        try
        {
            // DIAGNOSTIC (v0.5.38): a degenerate (0-col/0-row) resize from a
            // transient layout measurement is a suspect for upsetting the
            // agent/ConPTY — flag it instead of silently forwarding.
            if (size.Cols <= 0 || size.Rows <= 0)
            {
                Log.Warning("Degenerate terminal resize for {Project}: {Cols}x{Rows}", Context.Name, size.Cols, size.Rows);
            }
            _ptyChannel?.Resize(size.Cols, size.Rows);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnProgressChanged(object? sender, bool active)
    {
        _detector.NotifyProgress(active);
    }

    private void OnStateChanged(object? sender, SessionState state)
    {
        if (_headerText.Dispatcher.CheckAccess())
        {
            ApplyStateVisuals(state);
        }
        else
        {
            _headerText.Dispatcher.InvokeAsync(() => ApplyStateVisuals(state));
        }
    }

    private void ApplyStateVisuals(SessionState state)
    {
        _headerText.Foreground = StateColors.Brush(state);
        _headerText.FontWeight = state == SessionState.Burning ? FontWeights.SemiBold : FontWeights.Normal;
        if (state == SessionState.Dead)
        {
            ShowRekindleAffordance();
        }
        else
        {
            HideRekindleAffordance();
        }
        // First PTY output → boot done. Reveal WebView2 (was hidden so the
        // spinner could be visible) and hide the spinner. Burning, Embers, and
        // Dead all imply at least one byte arrived, so any of them is a valid
        // reveal trigger.
        if (state is SessionState.Burning or SessionState.Embers or SessionState.Dead)
        {
            if (_terminalView is not null && _terminalView.Element.Visibility != Visibility.Visible)
            {
                _terminalView.Element.Visibility = Visibility.Visible;

                // First reveal is the real focus opportunity. The tab-switch
                // focus call (MainWindow.OnTabSelectionChanged) fires while the
                // WebView2 is still Hidden, so xterm never takes focus and the
                // agent's first prompt ("trust this folder?") swallows Enter.
                // Now that the hwnd is actually shown, hand it the keyboard —
                // but only if this tab is the foreground one, so a background
                // tab lighting up doesn't steal focus from where the user is
                // typing. One dispatcher tick lets the hwnd finish attaching.
                if (_content.IsVisible)
                {
                    _content.Dispatcher.BeginInvoke(
                        new Action(FocusTerminal),
                        System.Windows.Threading.DispatcherPriority.Input);
                }
            }
            HideLoadingIndicator();
        }
    }

    private void OpenExplorer()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Context.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowFatal($"Cannot open Explorer: {ex.Message}");
        }
    }

    private void OpenProjectConfig()
    {
        try
        {
            var projectId = _currentConfig?.Id ?? Context.Name;
            // No meta path here on purpose: a tab does not know where the
            // central repo is, and the shared-fragment imports arrive with
            // firepit_blueprint_apply, which does.
            var scaffold = Firepit.Core.ProjectConfig.ProjectScaffolding.EnsureProjectScaffold(
                Context.Path, projectId);
            if (scaffold.ScaffoldCreated)
            {
                Log.Information(
                    "First scaffold for {Project}: gitignoreUpdated={Git}, claudeSeeded={Claude}, blanketIgnores=[{Blanket}]",
                    Context.Name, scaffold.GitignoreUpdated, scaffold.ClaudeMdSeeded, string.Join(", ", scaffold.BlanketIgnores));
                if (scaffold.BlanketIgnores.Count > 0)
                {
                    OfferBlanketIgnoreFix(scaffold.BlanketIgnores);
                }
            }
            Log.Information("Opening project config for {Project} at {Path}", Context.Name, scaffold.ConfigPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = scaffold.ConfigPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open project config for {Project}", Context.Name);
            ShowFatal($"Cannot open project config: {ex.Message}");
        }
    }

    /// <summary>
    /// The project's .gitignore blanket-ignores .firepit/ or .claude/, which
    /// hides the shared config (config.json, mcp.json, commands, agents) from
    /// git. Offer to disable the blanket lines and add the granular block.
    /// </summary>
    private void OfferBlanketIgnoreFix(IReadOnlyList<string> blanketIgnores)
    {
        var owner = Window.GetWindow(_content);
        var lines = string.Join(", ", blanketIgnores);
        var fix = MessageDialog.Show(
            owner,
            title: "Gitignore hides shared config",
            message:
                $"This project's .gitignore blanket-ignores {lines}, which keeps the shareable " +
                "Firepit/Claude config (config.json, .claude/mcp.json, commands, agents) out of git. " +
                "Fix it — comment those lines out and add the granular ignore block?",
            primaryLabel: "Fix .gitignore",
            secondaryLabel: "Leave it");
        if (!fix)
        {
            return;
        }
        try
        {
            Firepit.Core.ProjectConfig.ProjectScaffolding.MigrateBlanketIgnores(Context.Path);
            Log.Information("Migrated blanket gitignore for {Project}: {Lines}", Context.Name, lines);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Gitignore migration failed for {Project}", Context.Name);
            ShowFatal($"Could not update .gitignore: {ex.Message}");
        }
    }

    /// <summary>
    /// Open an external shell in the project directory. <paramref name="elevated"/>
    /// adds the <c>runas</c> verb so the shell launches with administrator
    /// rights (triggers a UAC prompt). Prefers Windows Terminal; falls back to
    /// PowerShell. A declined UAC prompt is silently ignored — it's a choice,
    /// not an error.
    /// </summary>
    private void OpenExternalShell(bool elevated = false)
    {
        var verb = elevated ? "runas" : string.Empty;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{Context.Path}\"",
                UseShellExecute = true,
                Verb = verb,
            });
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user dismissed the UAC prompt. Back out quietly.
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"Set-Location '{Context.Path}'\"",
                    UseShellExecute = true,
                    Verb = verb,
                });
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — user dismissed the UAC prompt.
            }
            catch (Exception ex)
            {
                ShowFatal($"Cannot open shell: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Open the project in the configured editor (settings.json →
    /// <c>shells.editor</c>, default <c>code</c>) with the project path as the
    /// only argument. A launcher that resolves to a cmd-shim on PATH — VS
    /// Code's <c>code.cmd</c> is the canonical case — runs through a hidden
    /// cmd.exe so no console window flashes; everything else (exe names, full
    /// paths) goes straight through ShellExecute.
    /// </summary>
    private void OpenEditor()
    {
        try
        {
            var shim = ResolveCmdShim(_editorCommand);
            System.Diagnostics.Process.Start(shim is not null
                ? new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c \"\"{shim}\" \"{Context.Path}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
                : new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _editorCommand,
                    Arguments = $"\"{Context.Path}\"",
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            ShowFatal($"Cannot open editor '{_editorCommand}': {ex.Message}");
        }
    }

    /// <summary>
    /// PATH lookup for a bare command name, returning the full path only when
    /// the first hit (shell resolution order: PATH dirs, then PATHEXT within
    /// each) is a .cmd/.bat shim. Names that resolve to an exe, and anything
    /// given as a path, return null — ShellExecute handles those fine.
    /// </summary>
    private static string? ResolveCmdShim(string command)
    {
        if (command.Contains('\\') || command.Contains('/'))
        {
            return null;
        }
        var candidates = System.IO.Path.HasExtension(command)
            ? [command]
            : new[] { command + ".com", command + ".exe", command + ".bat", command + ".cmd" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = System.IO.Path.Combine(dir.Trim(), candidate);
                    if (!System.IO.File.Exists(full)) continue;
                }
                catch (ArgumentException) { break; /* malformed PATH entry */ }
                var isShim = full.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                          || full.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                return isShim ? full : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Modal: list every shell command in the current config and ask the user
    /// to trust them. On approve, the project+hash is recorded in the trust
    /// ledger so subsequent clicks (and other shell commands in the same
    /// file) skip the prompt — until the file changes.
    /// </summary>
    private bool PromptForCommandsTrust(string hash)
    {
        if (_trustLedger is null) return true;
        var owner = Window.GetWindow(_content);
        var shellCmds = (_currentConfig?.Commands ?? [])
            .Where(c => c.Type == Firepit.Core.ProjectConfig.ProjectCommandType.Shell
                     && c.Disabled != true
                     && !string.IsNullOrEmpty(c.Command))
            .ToArray();
        // The commands ARE the decision — lead with them. The old wording put
        // three lines of prose about cloned repos ahead of the one thing worth
        // reading, so the list got skipped along with the warning.
        var list = string.Join('\n', shellCmds.Select(c =>
        {
            var args = c.Args is { Count: > 0 } a ? " " + string.Join(' ', a) : string.Empty;
            var elev = c.Elevated == true ? "   ⚠ as admin" : string.Empty;
            return $"  {c.Name}\n      {c.Command}{args}{elev}";
        }));

        var ok = MessageDialog.Show(
            owner,
            title: $"Run these commands from {Context.Name}?",
            message:
                (string.IsNullOrEmpty(list) ? "  (none declared)" : list) +
                "\n\nThey come from this project's .firepit/config.json and run as you." +
                "\nAllowing covers this exact file — any edit asks again.",
            primaryLabel: "Allow",
            secondaryLabel: "Cancel");
        if (ok)
        {
            _trustLedger.Trust(Context.Path, hash);
            Log.Information("Trusted commands in {Project} (hash {Hash})", Context.Name, hash[..16]);
            return true;
        }
        Log.Information("Trust declined for {Project} commands", Context.Name);
        return false;
    }

    /// <summary>
    /// Resolve the shell command's cwd: absolute path stays as-is; relative
    /// path joins onto the project root; null/empty defaults to project root.
    /// Issue #11 Phase A.
    /// </summary>
    private string ResolveCwd(Firepit.Core.ProjectConfig.ProjectCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Cwd)) return Context.Path;
        if (System.IO.Path.IsPathRooted(cmd.Cwd)) return cmd.Cwd;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Context.Path, cmd.Cwd));
    }

    private void RunCommand(Firepit.Core.ProjectConfig.ProjectCommand cmd)
    {
        try
        {
            switch (cmd.Type)
            {
                case Firepit.Core.ProjectConfig.ProjectCommandType.Shell:
                    RunShellCommand(cmd);
                    break;
                case Firepit.Core.ProjectConfig.ProjectCommandType.ClaudePrompt:
                    if (string.IsNullOrEmpty(cmd.Prompt) || _ptyChannel is null) return;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(cmd.Prompt + "\n");
                    _ = _ptyChannel.WriteAsync(bytes, _cts?.Token ?? CancellationToken.None);
                    Log.Information("Sent prompt to agent for '{Name}'", cmd.Name);
                    break;
                case Firepit.Core.ProjectConfig.ProjectCommandType.Url:
                    if (string.IsNullOrEmpty(cmd.Url)) return;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cmd.Url,
                        UseShellExecute = true,
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Command '{Name}' failed", cmd.Name);
            ShowCommandError(cmd, ex);
        }
    }

    /// <summary>
    /// Surface a failed toolbar command in a modal dialog. <see cref="ShowFatal"/>
    /// is useless here: it renders a WPF TextBlock inside the terminal area, but
    /// the WebView2 is an HwndHost and (airspace rule) paints over every WPF
    /// element — so over a live terminal the message is invisible. A modal
    /// dialog is its own top-level window, so it actually shows. The most common
    /// failure is a missing executable (Win32 error 2), so call that out
    /// explicitly with the resolved command name.
    /// </summary>
    private void ShowCommandError(Firepit.Core.ProjectConfig.ProjectCommand cmd, Exception ex)
    {
        var owner = Window.GetWindow(_content);
        var detail = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
            ? $"'{cmd.Command}' could not be started — the file was not found. " +
              "Is it installed and on your PATH? (e.g. 'pwsh' is PowerShell 7, which isn't installed by default — use 'powershell'.)"
            : ex.Message;
        MessageDialog.Show(
            owner,
            title: $"Command '{cmd.Name}' failed",
            message: detail,
            primaryLabel: "OK",
            secondaryLabel: null);
    }

    /// <summary>
    /// Right-click → "Stop". Kills the tracked process tree if alive.
    /// </summary>
    private void StopCommand(Firepit.Core.ProjectConfig.ProjectCommand cmd)
    {
        try
        {
            _commandRunner.Stop(cmd);
            RefreshCommandRunningState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stop for command '{Name}' failed", cmd.Name);
        }
    }

    /// <summary>
    /// Re-render the toolbar so running-state indicators / Stop menu items
    /// reflect the current CommandRunner state. Called when the runner emits
    /// state-changed (Spawn / process Exited / explicit Stop).
    /// </summary>
    private void RefreshCommandRunningState()
    {
        if (_disposed) return;
        _toolbar.SetCommands(_currentConfig?.Commands ?? [], _commandRunner.IsAlive);
    }

    private void RunShellCommand(Firepit.Core.ProjectConfig.ProjectCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.Command)) return;

        // Trust gate (issue #11). Shell commands can run arbitrary code with
        // user privileges — a cloned repo can ship anything in
        // .firepit/config.json. Prompt once per (project, config-file-hash);
        // any byte-level edit invalidates the previous trust.
        if (_trustLedger is not null)
        {
            var hash = Firepit.Core.ProjectConfig.CommandsTrustLedger.HashConfigFile(Context.Path);
            if (hash is not null && !_trustLedger.IsTrusted(Context.Path, hash))
            {
                if (!PromptForCommandsTrust(hash)) return;
            }
        }

        // Inline: write into the active PTY instead of spawning a window.
        // The session's shell/agent owns cwd + env, so Cwd/Env/Elevated are
        // intentionally ignored here — call them out in the log so a confused
        // user knows why their elevated flag did nothing.
        if (string.Equals(cmd.Window, "inline", StringComparison.OrdinalIgnoreCase))
        {
            RunShellInline(cmd);
            return;
        }

        // reuse:<id> — if a previous launch is still alive, focus it; only
        // spawn when nothing's running. Trust + confirm gates still apply on
        // first launch but a focus is a free action (already-trusted process).
        if (CommandRunner.TryParseReuse(cmd.Window, out _) && _commandRunner.IsAlive(cmd))
        {
            if (!_commandRunner.FocusExisting(cmd))
            {
                Log.Debug("Reuse focus for '{Name}' returned false — process exists but has no main window yet", cmd.Name);
            }
            return;
        }

        // Confirm before state-changing commands (capture-on writes the hosts
        // file, db drops, deploys). The user marks them with "confirm": true;
        // modal cancel just no-ops.
        if (cmd.Confirm == true)
        {
            var owner = Window.GetWindow(_content);
            var argsPreview = cmd.Args is { Count: > 0 } a0 ? " " + string.Join(' ', a0) : string.Empty;
            var elevatedNote = cmd.Elevated == true ? " (as administrator)" : string.Empty;
            var ok = MessageDialog.Show(
                owner,
                title: $"Run '{cmd.Name}'?",
                message: $"This will execute:\n\n  {cmd.Command}{argsPreview}\n\nin {ResolveCwd(cmd)}{elevatedNote}.",
                primaryLabel: "Run",
                secondaryLabel: "Cancel");
            if (!ok) return;
        }

        // Wrap the launch in cmd.exe when we need a conditional pause
        // (keepOpenOnError) OR when it's elevated: Windows' ShellExecute ignores
        // WorkingDirectory for a runas spawn and drops the child in system32, so
        // the wrapper's `cd /d` shim restores the project root (relative paths
        // in elevated commands were otherwise unresolvable). Only reachable here
        // (inline returned early above), so it's inherently windowed-shell-only.
        var keepOpen = cmd.KeepOpenOnError == true;
        var elevated = cmd.Elevated == true;
        var cwd = ResolveCwd(cmd);
        var wrap = keepOpen || elevated;
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = wrap
                ? Firepit.Core.ProjectConfig.ShellCommandLauncher.ShellExecutable
                : cmd.Command,
            Arguments = wrap
                ? Firepit.Core.ProjectConfig.ShellCommandLauncher.BuildWrappedArguments(cmd.Command!, cmd.Args, cwd, keepOpen)
                : (cmd.Args is { Count: > 0 } a ? string.Join(' ', a) : string.Empty),
            WorkingDirectory = cwd,
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
        };
        if (elevated)
        {
            // Windows: triggers UAC; ERROR_CANCELLED if user declines.
            psi.Verb = "runas";
        }
        if (cmd.Env is { Count: > 0 } env)
        {
            // UseShellExecute=true means we can only seed extra env vars via
            // the ProcessStartInfo.Environment dictionary before launch; nulls
            // remove (matches mcpOverrides semantics). Inheritance is automatic.
            foreach (var (k, v) in env)
            {
                if (v is null) psi.Environment.Remove(k);
                else            psi.Environment[k] = v;
            }
        }

        try
        {
            var proc = _commandRunner.Spawn(cmd, psi);
            Log.Information(
                "Ran shell command '{Name}' in {Path} (window={Window}, longRunning={Long}, tracked={Tracked}, pid={Pid}, elevated={Elev}, env+={EnvCount})",
                cmd.Name, psi.WorkingDirectory, cmd.Window ?? "new", cmd.LongRunning == true,
                CommandRunner.KeyOf(cmd) is not null, proc?.Id ?? -1, cmd.Elevated == true, cmd.Env?.Count ?? 0);
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — UAC declined. Treat as user choice.
            Log.Information("Shell command '{Name}' aborted at UAC prompt", cmd.Name);
        }
    }

    /// <summary>
    /// Write the shell command line into the active PTY so the session's
    /// shell/agent runs it. Args are joined with a single space — this is
    /// best-effort; the user accepts the same quoting rules they'd have at a
    /// real prompt. No PTY means no-op (with a log) — typically the user just
    /// hasn't activated the tab yet.
    /// </summary>
    private void RunShellInline(Firepit.Core.ProjectConfig.ProjectCommand cmd)
    {
        if (_ptyChannel is null)
        {
            Log.Information("Inline command '{Name}' skipped — no live PTY in {Project}", cmd.Name, Context.Name);
            return;
        }
        var line = cmd.Args is { Count: > 0 } a
            ? cmd.Command + " " + string.Join(' ', a)
            : cmd.Command ?? string.Empty;
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        _ = _ptyChannel.WriteAsync(bytes, _cts?.Token ?? CancellationToken.None);
        Log.Information("Sent inline shell command '{Name}' to PTY for {Project}", cmd.Name, Context.Name);
    }

    private void OpenQuickLink(ResolvedQuickLink link)
    {
        if (!link.Available)
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = link.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowFatal($"Cannot open {link.Name}: {ex.Message}");
        }
    }

    private void ShowFatal(string message)
    {
        HideLoadingIndicator();
        _statusText.Text = message;
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0x5C, 0x5C));
        if (!_terminalArea.Children.Contains(_statusText))
        {
            _terminalArea.Children.Add(_statusText);
        }
    }

    private static double ResolveFontSize(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double d ? d : fallback;

    private static (StackPanel Panel, RotateTransform Rotate, Storyboard Story) BuildLoadingIndicator(string projectName)
    {
        // Single source of truth for rotation center: RotateTransform's explicit
        // CenterX/Y in element-local coords. Do NOT also set RenderTransformOrigin —
        // WPF would compose the two and the spinner would orbit instead of spin.
        var rotate = new RotateTransform(0, 12, 12);
        var spinnerGeometry = (Geometry)Application.Current.FindResource("IconSpinnerArc");
        var spinner = new System.Windows.Shapes.Path
        {
            Data = spinnerGeometry,
            Stroke = StateColors.Brush(SessionState.Igniting),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = rotate,
        };

        var label = new TextBlock
        {
            Text = $"Igniting {projectName}…",
            Foreground = StateColors.Brush(SessionState.Igniting),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = ResolveFontSize("MediumFontSize", 13),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(spinner);
        panel.Children.Add(label);
        // WebView2 mounts on top of us; without this z-bump the spinner is
        // hidden behind the WebView's background even before any output paints.
        Panel.SetZIndex(panel, 10);

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, spinner);
        Storyboard.SetTargetProperty(animation,
            new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
        var story = new Storyboard();
        story.Children.Add(animation);

        return (panel, rotate, story);
    }

    private void StartSpinner()
    {
        try { _spinnerStoryboard.Begin(_loadingIndicator, isControllable: true); }
        catch { /* animation start is best-effort */ }
    }

    private void StopSpinner()
    {
        try { _spinnerStoryboard.Stop(_loadingIndicator); } catch { /* ignored */ }
    }

    private void ShowLoadingIndicator()
    {
        if (!_terminalArea.Children.Contains(_loadingIndicator))
        {
            _terminalArea.Children.Add(_loadingIndicator);
        }
        _loadingIndicator.Visibility = Visibility.Visible;
        StartSpinner();
    }

    private void HideLoadingIndicator()
    {
        StopSpinner();
        _loadingIndicator.Visibility = Visibility.Collapsed;
        if (_terminalArea.Children.Contains(_loadingIndicator))
        {
            _terminalArea.Children.Remove(_loadingIndicator);
        }
    }

    private Border BuildRekindleAffordance()
    {
        var label = new TextBlock
        {
            Text = "This session went out. Click to restart.",
            Foreground = StateColors.Brush(SessionState.Igniting),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = ResolveFontSize("TitleFontSize", 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(20, 12, 20, 12),
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x1A, 0x16, 0x12)),
            BorderBrush = StateColors.Brush(SessionState.Igniting),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = label,
        };
        border.MouseLeftButtonUp += (_, _) => _ = RekindleAsync(resume: false, confirmIfBurning: false);
        return border;
    }

    private void ShowRekindleAffordance()
    {
        if (!_terminalArea.Children.Contains(_rekindleAffordance))
        {
            _terminalArea.Children.Add(_rekindleAffordance);
        }
        _rekindleAffordance.Visibility = Visibility.Visible;
    }

    private void HideRekindleAffordance()
    {
        _rekindleAffordance.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-apply <c>.firepit/config.json</c> to this live session.
    /// Quick-links re-render via <see cref="TabToolbar.SetQuickLinks"/> (live,
    /// no agent restart). MCP activations and agent overrides can't be
    /// hot-swapped — Claude reads its MCP config once at startup, and the
    /// agent's env/args are baked into the launch spec — so a change there
    /// surfaces a non-modal "Restart needed" banner. Click → resume restart.
    /// </summary>
    public Task RefreshFromConfigAsync(Firepit.Core.ProjectConfig.ProjectConfig? newConfig)
    {
        if (_disposed) return Task.CompletedTask;

        try
        {
            _toolbar.SetQuickLinks(_quickLinks.ResolveForProject(Context));
            _toolbar.SetCommands(newConfig?.Commands ?? [], _commandRunner.IsAlive);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh quick-links / commands for {Project}", Context.Name);
        }

        if (NeedsRestart(_currentConfig, newConfig))
        {
            ShowConfigRestartAffordance();
            Log.Information("Config change for {Project} requires session restart", Context.Name);
        }
        _currentConfig = newConfig;
        return Task.CompletedTask;
    }

    private Border BuildConfigRestartAffordance()
    {
        var label = new TextBlock
        {
            Text = "Config changed — click to restart this session",
            Foreground = StateColors.Brush(SessionState.Igniting),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = ResolveFontSize("MediumFontSize", 13),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 6, 14, 6),
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2A, 0x21, 0x1A)),
            BorderBrush = StateColors.Brush(SessionState.Igniting),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = label,
        };
        Panel.SetZIndex(border, 50);
        border.MouseLeftButtonUp += (_, _) =>
        {
            HideConfigRestartAffordance();
            _ = RekindleAsync(resume: true, confirmIfBurning: false);
        };
        return border;
    }

    private void ShowConfigRestartAffordance()
    {
        if (!_terminalArea.Children.Contains(_configRestartAffordance))
        {
            _terminalArea.Children.Add(_configRestartAffordance);
        }
        _configRestartAffordance.Visibility = Visibility.Visible;
    }

    private void HideConfigRestartAffordance()
    {
        _configRestartAffordance.Visibility = Visibility.Collapsed;
    }

    private (Border Border, TextBlock Label) BuildMcpHealthAffordance()
    {
        var label = new TextBlock
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B)),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = ResolveFontSize("MediumFontSize", 13),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 6, 14, 6),
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2A, 0x21, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0x5C, 0x5C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "Click to dismiss. Edit %APPDATA%\\Firepit\\settings.json or the project's .firepit/config.json to fix.",
            Child = label,
        };
        Panel.SetZIndex(border, 60);
        border.MouseLeftButtonUp += (_, _) => HideMcpHealthAffordance();
        return (border, label);
    }

    private void ShowMcpHealthIssues(IReadOnlyList<McpHealthIssue> issues)
    {
        if (issues.Count == 0)
        {
            HideMcpHealthAffordance();
            return;
        }

        var summary = issues.Count == 1
            ? $"⚠ MCP server failed: {issues[0].ServerId} — {issues[0].Detail}"
            : $"⚠ {issues.Count} MCP servers failed: {string.Join(", ", issues.Select(i => i.ServerId))}";
        _mcpHealthLabel.Text = summary;
        _mcpHealthAffordance.ToolTip = string.Join("\n",
            issues.Select(i => $"{i.ServerId}: {i.Detail}")) +
            "\n\nClick to dismiss. Edit %APPDATA%\\Firepit\\settings.json or the project's .firepit/config.json to fix.";

        if (!_terminalArea.Children.Contains(_mcpHealthAffordance))
        {
            _terminalArea.Children.Add(_mcpHealthAffordance);
        }
        _mcpHealthAffordance.Visibility = Visibility.Visible;
    }

    private void HideMcpHealthAffordance()
    {
        _mcpHealthAffordance.Visibility = Visibility.Collapsed;
    }

    private static bool NeedsRestart(
        Firepit.Core.ProjectConfig.ProjectConfig? oldCfg,
        Firepit.Core.ProjectConfig.ProjectConfig? newCfg)
    {
        return !string.Equals(
            ProjectConfigFingerprint.ForRestart(oldCfg),
            ProjectConfigFingerprint.ForRestart(newCfg),
            StringComparison.Ordinal);
    }
}

internal static class StateColors
{
    public static Brush Brush(SessionState state) => state switch
    {
        SessionState.Cold     => Frozen(0x6B, 0x62, 0x58),
        SessionState.Igniting => Frozen(0xC9, 0x9A, 0x5C),
        SessionState.Burning  => Frozen(0xF5, 0xC9, 0x7B),
        SessionState.Embers   => Frozen(0x8C, 0x7A, 0x5C),
        SessionState.Dead     => Frozen(0x6B, 0x62, 0x58),
        _                     => Frozen(0xA8, 0x9F, 0x92),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
