using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Firepit.Adapters;
using Firepit.Core.Agents;
using Firepit.Core.Inbox;
using Firepit.Core.Jobs;
using Firepit.Core.Mcp;
using Firepit.Core.Platform;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Projects;
using Firepit.Core.QuickLinks;
using Firepit.Core.Secrets;
using Firepit.Core.Settings;
using Firepit.Core.State;
using Firepit.Core.Time;
using Firepit.Native;
using Firepit.Process;
using Firepit.Views;
using Serilog;

namespace Firepit;

public partial class MainWindow : Window
{
    private readonly IReadOnlyDictionary<string, IAgentAdapter> _adapters;
    private readonly ISettingsStore _settingsStore;
    private readonly Dictionary<string, (TabItem TabItem, SessionTab Session)> _openTabs = new(StringComparer.OrdinalIgnoreCase);

    // Projects whose tab was closed after a real session had run. Reopening
    // such a tab in the same app run should --continue rather than start a
    // fresh session — otherwise closing-and-reopening a tab silently drops the
    // user's conversation. Across app restarts, RestoreTabsFromState handles
    // resume instead; this set only needs to survive within one app run.
    private readonly HashSet<string> _resumableProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ProjectPickerItem> _pickerItems = new();
    private List<Project> _allProjects = new();

    private FirepitSettings _settings;
    private IQuickLinkService _quickLinks;
    private IMcpRegistry _mcpRegistry;
    private readonly IAgentMcpProjector _mcpProjector;
    private readonly ISecretResolver _secretResolver;
    private readonly IStateStore _stateStore;
    private readonly Firepit.Core.ProjectConfig.CommandsTrustLedger _commandsTrust;
    private readonly IProjectConfigStore _projectConfigStore = new JsonProjectConfigStore();
    private readonly Firepit.Core.Artifacts.IArtifactStore _artifactStore = new Firepit.Core.Artifacts.JsonArtifactStore();
    private readonly Dictionary<string, IProjectConfigWatcher> _configWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InboxWatcher> _inboxWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RunsWatcher> _runsWatchers = new(StringComparer.OrdinalIgnoreCase);
    private JobScheduler? _jobScheduler;
    private int _pendingMigrationCount;

    // True while RestoreTabsFromState is mid-loop. Suppresses StartDeferredTab
    // inside OnTabSelectionChanged so the spurious SelectionChanged re-fires
    // WPF emits during back-to-back Tabs.Items.Add can't accidentally start
    // a non-active tab. The active tab is started explicitly at the end of
    // the restore. Without this guard a 4-tab restore with claude-code
    // sessions ended up queueing two WebView2 inits behind a single 45 s cold
    // start — Firepit-ai (the actual active tab) timed out and the other tab
    // booted into a window that wasn't visible.
    private bool _restoring;

    // Drag-reorder state. _dragSource is the TabItem under cursor on
    // PreviewMouseDown; cleared on drag-start so the same press can't fire
    // twice. _dragStart is the press point in Tabs-local coords for hysteresis.
    private TabItem? _dragSource;
    private System.Windows.Point _dragStart;
    private const double DragThresholdPx = 8.0;

    // Debounces the continuous tab-layout persistence. state.json used to be
    // written only in OnClosing — which never runs when Windows kills the
    // process at OS shutdown/restart, so the "restored" layout could be days
    // stale. Every layout change now schedules a save; a hard kill loses at
    // most the last second.
    private DispatcherTimer? _stateSaveTimer;

    public MainWindow()
    {
        InitializeComponent();

        _adapters = new Dictionary<string, IAgentAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaudeCodeAdapter.AdapterId] = new ClaudeCodeAdapter(),
        };

        _settingsStore = new JsonSettingsStore();
        _settings = _settingsStore.Load();
        ApplyChromeMetricsFromResources();

        _stateStore = new JsonStateStore();
        _commandsTrust = new Firepit.Core.ProjectConfig.CommandsTrustLedger(_stateStore);
        RunProjectConfigMigrationIfNeeded();
        RunLegacyQuickLinksMigrationIfNeeded();
        ApplyPersistedWindowPlacement();

        _quickLinks = BuildQuickLinkService(_settings);
        _secretResolver = new CompositeSecretResolver(
            new EnvironmentSecretProvider(),
            new CredentialManagerSecretProvider());
        _mcpRegistry = new SettingsBackedMcpRegistry(
            _settings,
            _secretResolver,
            _projectConfigStore.Load,
            warn: msg => Log.Warning("McpRegistry: {Message}", msg));
        _mcpProjector = new ClaudeCodeMcpProjector();

        PickerList.ItemsSource = _pickerItems;
        InitializeArtifactPane();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        LocationChanged += (_, _) => ScheduleStateSave();
        SizeChanged += (_, _) => ScheduleStateSave();
        Application.Current.SessionEnding += OnOsSessionEnding;

        Tabs.AllowDrop = true;
        Tabs.DragOver  += OnTabsDragOver;
        Tabs.DragLeave += OnTabsDragLeave;
        Tabs.Drop      += OnTabsDrop;

        RegisterKeyboardShortcuts();
    }

    /// <summary>
    /// Window-level shortcuts. Chosen to coexist with bash readline inside the
    /// embedded terminal — Ctrl+W and Ctrl+T are reserved by readline, so the
    /// Firepit equivalents take a Shift. Ctrl+1..9 conflicts with some apps, so
    /// tab-jump uses Ctrl+Alt+N (Windows-Terminal convention). Ctrl+Tab and
    /// Ctrl+Shift+Tab are safe (terminals don't bind them).
    /// </summary>
    private void RegisterKeyboardShortcuts()
    {
        Bind(new KeyGesture(System.Windows.Input.Key.T, ModifierKeys.Control | ModifierKeys.Shift),
            (_, _) => ProjectPicker.IsOpen = !ProjectPicker.IsOpen);
        Bind(new KeyGesture(System.Windows.Input.Key.W, ModifierKeys.Control | ModifierKeys.Shift),
            (_, _) =>
            {
                if (Tabs.SelectedItem is TabItem t)
                {
                    _ = CloseTabAsync(t, confirmIfBurning: true);
                }
            });
        Bind(new KeyGesture(System.Windows.Input.Key.Tab, ModifierKeys.Control),
            (_, _) => CycleTab(+1));
        Bind(new KeyGesture(System.Windows.Input.Key.Tab, ModifierKeys.Control | ModifierKeys.Shift),
            (_, _) => CycleTab(-1));
        // Ctrl+PgDn / Ctrl+PgUp as alternates — browser-style, some users
        // muscle-memory these instead of Ctrl+Tab.
        Bind(new KeyGesture(System.Windows.Input.Key.PageDown, ModifierKeys.Control),
            (_, _) => CycleTab(+1));
        Bind(new KeyGesture(System.Windows.Input.Key.PageUp, ModifierKeys.Control),
            (_, _) => CycleTab(-1));
        for (var i = 1; i <= 9; i++)
        {
            var n = i;
            Bind(new KeyGesture(System.Windows.Input.Key.D0 + i, ModifierKeys.Control | ModifierKeys.Alt),
                (_, _) => JumpToTab(n));
        }
    }

    private void Bind(KeyGesture gesture, ExecutedRoutedEventHandler handler)
    {
        var cmd = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(cmd, handler));
        InputBindings.Add(new KeyBinding(cmd, gesture));
    }

    private void CycleTab(int direction)
    {
        if (Tabs.Items.Count < 2) return;
        var idx = Tabs.SelectedIndex;
        if (idx < 0) idx = 0;
        var next = ((idx + direction) % Tabs.Items.Count + Tabs.Items.Count) % Tabs.Items.Count;
        ((TabItem)Tabs.Items[next]!).IsSelected = true;
    }

    private void JumpToTab(int n)
    {
        if (n < 1 || n > Tabs.Items.Count) return;
        ((TabItem)Tabs.Items[n - 1]!).IsSelected = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowDarkMode.EnableForWindow(this);
    }

    /// <summary>
    /// Restore the previously-saved window position and size. Applied in the
    /// constructor (before the window first appears) so there is no
    /// CenterScreen → saved-rect flicker. Validates that the rect intersects
    /// the current virtual screen — if the monitor it came from is gone
    /// (laptop disconnected from a dock), falls back to the XAML defaults.
    /// </summary>
    private void ApplyPersistedWindowPlacement()
    {
        var placement = _stateStore.Load().Window;
        if (placement is null) return;

        if (!IsOnScreen(placement))
        {
            Log.Information("Saved window placement is offscreen — falling back to CenterScreen");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnScreen(WindowPlacement p)
    {
        // At least 100x40 px of the window must intersect the virtual screen,
        // otherwise the user has no way to drag it back into view.
        var screen = new System.Windows.Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var win = new System.Windows.Rect(p.Left, p.Top, p.Width, p.Height);
        var visible = System.Windows.Rect.Intersect(screen, win);
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 40;
    }

    /// <summary>
    /// Snapshot the current window bounds for persistence. When the window
    /// is maximized, <see cref="System.Windows.Window.RestoreBounds"/> gives
    /// the un-maximized rect — that's what we save, so the next launch
    /// remembers both the maximized state *and* the size to restore to.
    /// </summary>
    private WindowPlacement CaptureWindowPlacement()
    {
        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new System.Windows.Rect(Left, Top, Width, Height);
        // Window minimized at close → RestoreBounds gives the pre-minimize
        // rect, which is what we want too.
        if (bounds.IsEmpty || double.IsNaN(bounds.Left))
        {
            bounds = new System.Windows.Rect(Left, Top, Width, Height);
        }
        return new WindowPlacement(
            Left: bounds.Left,
            Top: bounds.Top,
            Width: bounds.Width,
            Height: bounds.Height,
            IsMaximized: maximized);
    }

    private void ApplyChromeMetricsFromResources()
    {
        // App.xaml.cs writes CaptionPixelHeight before any Window loads. Mirror that
        // value into the title-bar row and WindowChrome so caption, tabs, and the
        // drag region all line up at any font size.
        if (TryGetDouble("CaptionPixelHeight", out var capH))
        {
            CaptionRow.Height = new System.Windows.GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null)
            {
                chrome.CaptionHeight = capH;
            }
        }
    }

    private bool TryGetDouble(string key, out double value)
    {
        if (TryFindResource(key) is double d) { value = d; return true; }
        value = 0; return false;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaxRestoreButton.Style = (Style)FindResource(
            WindowState == WindowState.Maximized
                ? "CaptionRestoreButton"
                : "CaptionMaximizeButton");
        MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";

        // WPF custom-chrome windows extend their client rect past the work
        // area by ResizeBorderThickness on every edge when maximized — a known
        // platform quirk. Without compensation, the 2 px yellow accent at the
        // top of the selected tab is the first thing to fall off-screen.
        // Add a matching margin on the root grid only while maximized.
        if (Content is FrameworkElement root)
        {
            if (WindowState == WindowState.Maximized)
            {
                var border = System.Windows.Shell.WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness
                             ?? new Thickness(6);
                root.Margin = border;
            }
            else
            {
                root.Margin = default;
            }
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReloadProjectList();
        if (_settings.Tabs.PersistAcrossRestarts)
        {
            RestoreTabsFromState();
        }
        if (_pendingMigrationCount > 0)
        {
            ShowToast($"Migrated {_pendingMigrationCount} project(s) to .firepit/config.json");
            _pendingMigrationCount = 0;
        }
        MaybePromptForMetaProject();
        StartJobScheduler();

        // Knowledge indexing + search (ROADMAP M9). After ReloadProjectList so
        // the first scope sync sees the full project list.
        InitializeKnowledgeService();

        // Start the MCP host now that this MainWindow (the IMcpBackend) exists.
        // App.OnStartup can't do this — StartupUri-based window construction
        // hadn't happened yet at that point in v0.5.13–v0.5.15, which is why
        // the firepit MCP server was unreachable. Issue #12.
        (Application.Current as App)?.EnsureMcpHostStarted(this);

        // Background update checks (VS-Code-style): caption-bar ember badge when
        // a newer GitHub release is found. Opt out via updates.checkForUpdates.
        StartUpdateChecks();
    }

    private async void StartJobScheduler()
    {
        try
        {
            var platform = _settings.Platform ?? PlatformSettings.Defaults;
            var history = new JsonJobHistoryStore(
                retention: TimeSpan.FromDays(Math.Max(1, platform.RunRetentionDays)),
                log: msg => Log.Warning("JobHistory: {Message}", msg));
            // Default spillover factory drops stdout-<guid>.log next to the
            // run record — perfect for production. No project lookup needed.
            var runner = new Firepit.Process.ProcessJobRunner();
            var source = new FileSystemJobScheduleSource(
                projectsRoot: _settings.ProjectsRoot,
                configStore: _projectConfigStore,
                warn: (project, message) => Log.Warning("Job source [{Project}]: {Message}", project, message));

            _jobScheduler = new JobScheduler(
                source, runner, history, new SystemActivityClock(),
                log: msg => Log.Information("JobScheduler: {Message}", msg));
            await _jobScheduler.StartAsync(CancellationToken.None);
            Log.Information("Job scheduler started; root={Root}", _settings.ProjectsRoot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start job scheduler");
            _jobScheduler = null;
        }
    }


    private void MaybePromptForMetaProject()
    {
        var platform = _settings.Platform ?? PlatformSettings.Defaults;
        if (platform.MetaProjectPromptShown) return;

        var bootstrapper = new MetaProjectBootstrapper();
        if (bootstrapper.Exists(_settings.ProjectsRoot))
        {
            // Folder already exists — nothing to do, but record that we
            // checked so subsequent launches skip the inspection.
            PersistMetaProjectPromptShown();
            return;
        }

        var create = MessageDialog.Show(
            owner: this,
            title: "Set up Firepit central project?",
            message: $"Firepit can create a hidden '.firepit' project at:\n\n  {_settings.ProjectsRoot}\\.firepit\n\n" +
                     "It's where Claude can manage settings across all your projects, plus a place for cross-project notes and inbox.\n\n" +
                     "You can also do this later from Settings.",
            primaryLabel: "Create",
            secondaryLabel: "Not now");

        PersistMetaProjectPromptShown();

        if (!create) return;

        try
        {
            var written = bootstrapper.Bootstrap(_settings.ProjectsRoot);
            Log.Information("Meta-project seeded: {Count} files written under {Path}",
                written.Count, bootstrapper.GetMetaProjectPath(_settings.ProjectsRoot));
            ShowToast($"Created .firepit central project ({written.Count} files)");
            // Refresh discovery so the new project shows up immediately.
            ReloadProjectList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Meta-project bootstrap failed");
            ShowToast($"Could not create .firepit: {ex.Message}", isError: true);
        }
    }

    private void PersistMetaProjectPromptShown()
    {
        try
        {
            var current = _settings.Platform ?? PlatformSettings.Defaults;
            _settings = _settings with { Platform = current with { MetaProjectPromptShown = true } };
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist MetaProjectPromptShown");
        }
    }

    private void ReloadProjectList()
    {
        var rawManual = (_settings.Projects ?? []).ToList();

        // Prune orphaned manual entries — a renamed or deleted project folder
        // leaves a dead registry block (the folder is gone but its parent still
        // exists). Entries whose whole drive/parent is missing are treated as
        // merely offline and kept, so a briefly-unavailable network/cloud mount
        // doesn't silently lose projects.
        var classified = rawManual
            .Select(p => (Entry: p, Status: ProjectRegistryHygiene.Classify(p.Path)))
            .ToList();
        var orphans = classified.Where(c => c.Status == ManualEntryStatus.Orphaned).Select(c => c.Entry).ToList();
        if (orphans.Count > 0)
        {
            rawManual = classified.Where(c => c.Status != ManualEntryStatus.Orphaned).Select(c => c.Entry).ToList();
        }

        // Heal entries persisted with a blank name. A UNC share root (\\nas\music)
        // used to derive an empty name, and that blank was written to
        // settings.json — so the nameless tab came back on every restart even
        // after the derivation itself was fixed. Also guards ToDictionary(p =>
        // p.Name) in the tab-restore path, which would throw on two blank names.
        var healed = new List<string>();
        for (var i = 0; i < rawManual.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(rawManual[i].Name))
            {
                continue;
            }
            var derived = ProjectNaming.DeriveName(rawManual[i].Path);
            if (string.IsNullOrWhiteSpace(derived))
            {
                continue;
            }
            rawManual[i] = rawManual[i] with { Name = derived };
            healed.Add(derived);
        }

        if (orphans.Count > 0 || healed.Count > 0)
        {
            _settings = _settings with { Projects = rawManual };
            try
            {
                _settingsStore.Save(_settings);
                if (orphans.Count > 0)
                {
                    var names = string.Join(", ", orphans.Select(o => o.Name));
                    Log.Information("Pruned {Count} orphaned project registry entr(ies): {Names}", orphans.Count, names);
                    ShowToast($"Removed {orphans.Count} stale project entr{(orphans.Count == 1 ? "y" : "ies")} ({names}) — folder no longer exists");
                }
                if (healed.Count > 0)
                {
                    Log.Information("Derived a name for {Count} unnamed project registry entr(ies): {Names}",
                        healed.Count, string.Join(", ", healed));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist project registry repairs");
            }
        }

        var manualEntries = rawManual
            .Select(MapManualProject)
            .ToArray();

        var discovery = new ProjectDiscovery(_adapters.Values);
        _allProjects = discovery.Scan(_settings.ProjectsRoot, manualEntries).ToList();

        Log.Information("Project list loaded: root={Root}, found={Count}",
            _settings.ProjectsRoot, _allProjects.Count);
        if (_allProjects.Count == 0 && !Directory.Exists(_settings.ProjectsRoot))
        {
            ShowToast($"Projects root not found: {_settings.ProjectsRoot}", isError: true);
        }

        SyncKnowledgeScopes();
    }

    private void RestoreTabsFromState()
    {
        var state = _stateStore.Load();
        if (state.Tabs.Count == 0)
        {
            return;
        }

        _restoring = true;
        TabItem? activeTabItem = null;
        try
        {
            var byName = _allProjects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var tab in state.Tabs)
            {
                if (!byName.TryGetValue(tab.ProjectName, out var project))
                {
                    continue;
                }
                var (tabItem, _) = AddSessionTab(project, resume: tab.LastSessionResumable, deferred: true);
                if (string.Equals(tab.ProjectName, state.ActiveTabProjectName, StringComparison.OrdinalIgnoreCase))
                {
                    activeTabItem = tabItem;
                }
            }

            // Decide which tab to focus. Saved active-tab wins; fallback to the
            // last tab in the list (pre-v0.5.3 behaviour) so users who upgrade
            // mid-session still see something sensible.
            activeTabItem ??= Tabs.Items.OfType<TabItem>().LastOrDefault();
            if (activeTabItem is not null)
            {
                activeTabItem.IsSelected = true;
            }
        }
        finally
        {
            _restoring = false;
        }

        // Now that the restore loop is done and the guard is off, start ONLY
        // the active tab. Every selection-change event during the loop was
        // suppressed by _restoring — without this explicit kick the active
        // tab would stay deferred forever.
        if (activeTabItem?.Tag is SessionTab activeSession)
        {
            Log.Information("Restore: explicitly starting active tab {Project}", activeSession.Context.Name);
            StartDeferredTab(activeSession);
        }
    }

    /// <summary>
    /// Wake a deferred-restore tab. Resume flag lives on the SessionTab
    /// itself (<see cref="SessionTab.PendingResume"/>), so this is now a
    /// thin pass-through to the tab's own RestartIfPending — which is
    /// resilient to phantom cancel-restart cycles (the bug v0.5.20 nails
    /// shut). Returns true if a start was attempted.
    /// </summary>
    private bool StartDeferredTab(SessionTab session)
    {
        if (session.IsStarted && session.State != Firepit.Core.Sessions.SessionState.Dead)
        {
            return false;
        }
        _ = session.RestartIfPending();
        return true;
    }

    public void SummonByName(string projectName)
    {
        if (_allProjects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)) is { } project)
        {
            OpenSessionTab(project, resume: false);
        }
    }

    private Project MapManualProject(ProjectSettings source) => new(
        // Belt-and-braces: even if the settings.json repair didn't stick, never
        // surface a nameless tab — derive from the path instead.
        Name: string.IsNullOrWhiteSpace(source.Name) ? ProjectNaming.DeriveName(source.Path) : source.Name,
        Path: source.Path,
        AdapterId: ClaudeCodeAdapter.AdapterId,
        AgentCommandOverride: source.AgentCommand,
        AgentArgsOverride: source.AgentArgs);

    private IQuickLinkService BuildQuickLinkService(FirepitSettings settings)
    {
        var globals = (settings.QuickLinks ?? [])
            .Select(MapLinkSetting)
            .ToArray();

        var legacyPerProject = (settings.Projects ?? [])
            .Where(p => p.QuickLinks is { Count: > 0 })
            .ToDictionary(p => p.Path, p => p.QuickLinks!, StringComparer.OrdinalIgnoreCase);

        return new QuickLinkService(
            globalDefaults: globals,
            // Per-project .firepit/config.json wins over the legacy
            // settings.Projects[].QuickLinks. Migration strips the legacy
            // entries on first launch — the fallback only matters during the
            // transition or when a user hand-edits settings.json.
            projectOverrides: ctx =>
            {
                var projectConfig = _projectConfigStore.Load(ctx.Path);
                if (projectConfig?.QuickLinks is { Count: > 0 } links)
                {
                    return links.Select(MapProjectQuickLink).ToArray();
                }
                return legacyPerProject.TryGetValue(ctx.Path, out var overrides)
                    ? overrides.Select(MapLinkSetting).ToArray()
                    : [];
            });
    }

    private static QuickLinkEntry MapProjectQuickLink(ProjectQuickLink source) => new(
        Name: source.Name,
        UrlTemplate: source.Url,
        Target: source.Target == QuickLinkTargetSetting.External ? QuickLinkTarget.External : QuickLinkTarget.SubTab,
        Icon: source.Icon,
        Disabled: source.Disabled ?? false);

    /// <summary>
    /// One-shot strip of the two legacy default quickLinks that pre-v0.5.0
    /// Firepit hardcoded into every fresh settings.json — issue #14. Both
    /// pointed at infrastructure that's NOT a default-install assumption:
    /// <list type="bullet">
    ///   <item>GitHub → <c>github.com/chloe-dream/{projectName}</c> (the maintainer's org)</item>
    ///   <item>Fishbowl → <c>localhost:7180/p/{projectName}</c> (a soft-wired optional integration that needs per-project provisioning)</item>
    /// </list>
    /// Removed only on exact name+url match — anyone who customised either
    /// entry keeps theirs. A toast tells the user what happened so they can
    /// re-add via Settings if they actually want the link.
    /// </summary>
    private void RunLegacyQuickLinksMigrationIfNeeded()
    {
        AppState state;
        try { state = _stateStore.Load(); }
        catch { return; }
        if (state.LegacyQuickLinksMigrationDone) return;

        var existing = (_settings.QuickLinks ?? []).ToList();
        if (existing.Count == 0)
        {
            try { _stateStore.Save(state with { LegacyQuickLinksMigrationDone = true }); }
            catch { /* best effort */ }
            return;
        }

        const string LegacyGitHubUrl   = "https://github.com/chloe-dream/{projectName}";
        const string LegacyFishbowlUrl = "https://localhost:7180/p/{projectName}";

        var removed = new List<string>();
        var kept = new List<QuickLinkSettings>(existing.Count);
        foreach (var link in existing)
        {
            var isLegacyGitHub   = string.Equals(link.Name, "GitHub",   StringComparison.OrdinalIgnoreCase)
                                && string.Equals(link.Url,  LegacyGitHubUrl,   StringComparison.OrdinalIgnoreCase);
            var isLegacyFishbowl = string.Equals(link.Name, "Fishbowl", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(link.Url,  LegacyFishbowlUrl, StringComparison.OrdinalIgnoreCase);
            if (isLegacyGitHub || isLegacyFishbowl)
            {
                removed.Add(link.Name);
            }
            else
            {
                kept.Add(link);
            }
        }

        if (removed.Count > 0)
        {
            _settings = _settings with { QuickLinks = kept };
            try
            {
                _settingsStore.Save(_settings);
                Log.Information("Removed legacy default quickLinks: {Names}", string.Join(", ", removed));
                // Defer the toast until MainWindow's actually loaded.
                Loaded += (_, _) => ShowToast(
                    $"Removed legacy default quick-link(s): {string.Join(", ", removed)}. " +
                    "Re-add via Settings → Quick-links if you actually use them.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not save settings after legacy quickLinks strip");
            }
        }

        try { _stateStore.Save(state with { LegacyQuickLinksMigrationDone = true }); }
        catch { /* best effort */ }
    }

    private void RunProjectConfigMigrationIfNeeded()
    {
        AppState state;
        try
        {
            state = _stateStore.Load();
        }
        catch
        {
            // Couldn't read state — skip migration this launch, retry next time.
            return;
        }

        if (state.ProjectConfigMigrationDone) return;
        if (_settings.Projects is null || _settings.Projects.Count == 0)
        {
            // No legacy entries to migrate — flag as done so we don't keep checking.
            try { _stateStore.Save(state with { ProjectConfigMigrationDone = true }); }
            catch { /* best effort */ }
            return;
        }

        try
        {
            var migrator = new ProjectConfigMigrator(_projectConfigStore);
            var result = migrator.Migrate(_settings);
            if (result.MigratedCount > 0)
            {
                ProjectConfigMigrator.BackupSettingsFile(((JsonSettingsStore)_settingsStore).SettingsPath);
                _settingsStore.Save(result.Settings);
                _settings = result.Settings;
                _pendingMigrationCount = result.MigratedCount;
                Log.Information("Migrated {Count} project(s) to .firepit/config.json", result.MigratedCount);
            }
            _stateStore.Save(state with { ProjectConfigMigrationDone = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Project-config migration failed; will retry next launch");
        }
    }

    private static QuickLinkEntry MapLinkSetting(QuickLinkSettings source) => new(
        Name: source.Name,
        UrlTemplate: source.Url,
        Target: source.Target == QuickLinkTargetSetting.External ? QuickLinkTarget.External : QuickLinkTarget.SubTab,
        Icon: source.Icon,
        Disabled: source.Disabled ?? false);

    /// <summary>
    /// Open a project tab from a user gesture (picker, folder browse). When a
    /// previous conversation could be resumed — the tab was closed earlier
    /// this run, or the agent left a transcript on disk — ask whether to
    /// resume or start fresh instead of silently deciding; Esc opens nothing.
    /// MCP-driven opens (<see cref="SummonByName"/>) stay prompt-free: an
    /// agent must never block on a modal dialog.
    /// </summary>
    private void OpenSessionTabInteractive(Project project)
    {
        var resumable = !_openTabs.ContainsKey(project.Path)
            && (_resumableProjects.Contains(project.Path)
                || (_adapters.TryGetValue(project.AdapterId, out var adapter)
                    && adapter.HasResumableSession(new ProjectContext(project))));
        if (!resumable)
        {
            OpenSessionTab(project, resume: false);
            return;
        }

        var choice = MessageDialog.ShowChoice(
            this,
            title: "Resume last session?",
            message: $"{project.Name} has a previous session.\n\nResume it, or start a new one?",
            primaryLabel: "Resume last",
            secondaryLabel: "New session");
        if (choice == MessageChoice.Dismissed)
        {
            return;
        }
        // The explicit choice overrides the silent close-and-reopen
        // auto-resume in OpenSessionTab — drop the flag either way.
        _resumableProjects.Remove(project.Path);
        OpenSessionTab(project, resume: choice == MessageChoice.Primary);
    }

    private void OpenSessionTab(Project project, bool resume)
    {
        if (_openTabs.TryGetValue(project.Path, out var existing))
        {
            existing.TabItem.IsSelected = true;
            // If the existing tab is a deferred-restore that hasn't actually
            // started yet (or got stuck in a cancelled state), clicking it
            // from the project list should wake it with its persisted resume
            // flag — not silently switch to a dead tab.
            if (!existing.Session.IsStarted || existing.Session.State == Firepit.Core.Sessions.SessionState.Dead)
            {
                _ = existing.Session.RestartIfPending();
            }
            return;
        }
        // A tab that was closed after running a session resumes on reopen, so
        // the user doesn't lose their conversation by closing-and-reopening.
        var effectiveResume = resume || _resumableProjects.Contains(project.Path);
        AddSessionTab(project, effectiveResume, deferred: false);
    }

    /// <summary>
    /// Build the tab UI and register it in <see cref="_openTabs"/>. In eager
    /// mode the session starts immediately and the tab is focused. In deferred
    /// mode (used by <see cref="RestoreTabsFromState"/>) the resume flag is
    /// stashed for later; the session only starts when the user clicks the tab.
    /// </summary>
    private (TabItem TabItem, SessionTab Session) AddSessionTab(Project project, bool resume, bool deferred)
    {
        if (!_adapters.TryGetValue(project.AdapterId, out var adapter))
        {
            Log.Warning("No adapter registered for project {Project} (adapterId={AdapterId})",
                project.Name, project.AdapterId);
            ShowToast($"No adapter for {project.Name}", isError: true);
            throw new InvalidOperationException($"No adapter for {project.Name}");
        }
        Log.Information("Opening tab for project {Project} (resume={Resume}, deferred={Deferred}) via adapter {Adapter}",
            project.Name, resume, deferred, adapter.Id);

        var initialConfig = SafeLoadProjectConfig(project.Path);
        var session = new SessionTab(
            new ProjectContext(project),
            adapter,
            _quickLinks,
            _mcpRegistry,
            _mcpProjector,
            terminalTheme: _settings.Terminal,
            terminalFontSize: (_settings.Ui ?? UiSettings.Defaults).ResolvedFontSize,
            initialConfig: initialConfig,
            trustLedger: _commandsTrust);
        var tabItem = new TabItem
        {
            Header = session.Header,
            Tag = session,
            ToolTip = project.Path,
            ContextMenu = BuildTabContextMenu(),
        };
        tabItem.PreviewMouseLeftButtonDown += OnTabPreviewMouseDown;
        tabItem.PreviewMouseMove           += OnTabPreviewMouseMove;

        Tabs.Items.Add(tabItem);
        Tabs.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        _openTabs[project.Path] = (tabItem, session);
        TryAttachConfigWatcher(project.Path, session);
        TryAttachInboxWatcher(project.Path, session);
        TryAttachRunsWatcher(project.Path, session, initialConfig);
        ScheduleStateSave();

        if (deferred)
        {
            // Resume flag travels with the tab, not in a sidecar dict —
            // survives the SelectionChanged race that used to lose --continue
            // on every restored non-active tab.
            session.PendingResume = resume;
        }
        else
        {
            tabItem.IsSelected = true;
            StartSession(session, resume);
        }
        return (tabItem, session);
    }

    private static void StartSession(SessionTab session, bool resume)
    {
        if (resume)
        {
            _ = session.RekindleAsync(resume: true, confirmIfBurning: false);
        }
        else
        {
            _ = session.EnsureInitializedAsync();
        }
    }

    private ContextMenu BuildTabContextMenu()
    {
        var menu = new ContextMenu();
        var close = new MenuItem { Header = "Close" };
        close.Click += (s, _e) =>
        {
            if (((MenuItem)s!).Parent is ContextMenu m && m.PlacementTarget is TabItem t)
            {
                _ = CloseTabAsync(t, confirmIfBurning: true);
            }
        };
        var closeOthers = new MenuItem { Header = "Close others" };
        closeOthers.Click += (s, _e) =>
        {
            if (((MenuItem)s!).Parent is ContextMenu m && m.PlacementTarget is TabItem keep)
            {
                _ = CloseOthersAsync(keep);
            }
        };
        var closeAll = new MenuItem { Header = "Close all" };
        closeAll.Click += (_s, _e) => { _ = CloseAllAsync(); };
        menu.Items.Add(close);
        menu.Items.Add(closeOthers);
        menu.Items.Add(closeAll);
        return menu;
    }

    private async Task CloseOthersAsync(TabItem keep)
    {
        var victims = Tabs.Items.OfType<TabItem>().Where(t => !ReferenceEquals(t, keep)).ToArray();
        foreach (var t in victims)
        {
            await CloseTabAsync(t, confirmIfBurning: true);
        }
    }

    private async Task CloseAllAsync()
    {
        var victims = Tabs.Items.OfType<TabItem>().ToArray();
        foreach (var t in victims)
        {
            await CloseTabAsync(t, confirmIfBurning: true);
        }
    }

    private Firepit.Core.ProjectConfig.ProjectConfig? SafeLoadProjectConfig(string path)
    {
        try { return _projectConfigStore.Load(path); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load .firepit/config.json for {Path}", path);
            return null;
        }
    }

    private void TryAttachConfigWatcher(string projectPath, SessionTab session)
    {
        if (!_settings.Tabs.AutoReloadOnConfigChange) return;
        if (_configWatchers.ContainsKey(projectPath)) return;

        try
        {
            var watcher = new Firepit.ProjectConfig.FileSystemProjectConfigWatcher(
                projectPath, _projectConfigStore, Dispatcher);
            watcher.ConfigChanged += (_, cfg) =>
            {
                _ = session.RefreshFromConfigAsync(cfg);
                _jobScheduler?.InvalidateProject(projectPath);
            };
            watcher.Start();
            _configWatchers[projectPath] = watcher;
            Log.Information("Config watcher started for {Path}", projectPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start config watcher for {Path}", projectPath);
        }
    }

    private void DisposeConfigWatcher(string projectPath)
    {
        if (_configWatchers.Remove(projectPath, out var watcher))
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
    }

    private void TryAttachInboxWatcher(string projectPath, SessionTab session)
    {
        if (!(_settings.Platform ?? PlatformSettings.Defaults).InboxBadgesEnabled) return;
        if (_inboxWatchers.ContainsKey(projectPath)) return;

        try
        {
            var watcher = new InboxWatcher(projectPath);
            // Two distinct UI surfaces:
            //   - Tab-header badge ⇐ NewSinceSeen   ("arrived while away")
            //   - Toolbar button   ⇐ Unpending      ("not yet processed")
            watcher.NewSinceSeenCountChanged += (_, count) =>
                Dispatcher.InvokeAsync(() => session.SetInboxBadge(count));
            watcher.UnpendingCountChanged += (_, count) =>
                Dispatcher.InvokeAsync(() => session.SetInboxToolbarCount(count));
            _inboxWatchers[projectPath] = watcher;
            // Apply initial counts synchronously so an existing inbox shows up
            // without waiting for a filesystem event.
            session.SetInboxBadge(watcher.NewSinceSeenCount);
            session.SetInboxToolbarCount(watcher.UnpendingCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start inbox watcher for {Path}", projectPath);
        }
    }

    private void DisposeInboxWatcher(string projectPath)
    {
        if (_inboxWatchers.Remove(projectPath, out var watcher))
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
    }

    private void TryAttachRunsWatcher(string projectPath, SessionTab session,
        Firepit.Core.ProjectConfig.ProjectConfig? initialConfig)
    {
        var platform = _settings.Platform ?? PlatformSettings.Defaults;
        if (!platform.RunBadgesEnabled) return;
        if (_runsWatchers.ContainsKey(projectPath)) return;

        var policy = initialConfig?.Runs?.BadgePolicy ?? platform.RunBadgePolicy;

        try
        {
            var watcher = new RunsWatcher(projectPath, policy);
            watcher.UnreadCountChanged += (_, count) =>
                Dispatcher.InvokeAsync(() => session.SetRunsBadge(count, OpenRunsOnClick(projectPath)));
            _runsWatchers[projectPath] = watcher;
            session.SetRunsBadge(watcher.UnreadCount, OpenRunsOnClick(projectPath));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start runs watcher for {Path}", projectPath);
        }
    }

    private void DisposeRunsWatcher(string projectPath)
    {
        if (_runsWatchers.Remove(projectPath, out var watcher))
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
    }

    private MouseButtonEventHandler OpenRunsOnClick(string projectPath) =>
        (_, _) =>
        {
            try
            {
                if (_runsWatchers.TryGetValue(projectPath, out var watcher))
                {
                    watcher.MarkAllSeen();
                }
                var runsPath = System.IO.Path.Combine(projectPath, ".firepit", "runs");
                System.IO.Directory.CreateDirectory(runsPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{runsPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open runs folder");
            }
        };

    // (OpenInboxOnClick removed — v0.5.15 replaced the "click badge → opens
    // Explorer, badge stays" UX with two-tier semantics: clicking the
    // tab-header badge implicitly activates the tab (header click selects
    // tab → OnTabSelectionChanged → InboxWatcher.MarkAsSeen → badge clears),
    // and a separate always-visible Inbox toolbar button is the entry point
    // for "process pending messages with Claude".)

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Active tab is part of the persisted layout (ActiveTabProjectName).
        ScheduleStateSave();

        if (Tabs.SelectedItem is TabItem selected)
        {
            // Keep the active tab visible when the strip is scrolled past it
            // (Ctrl+Tab into an off-screen tab, or a freshly-opened tab beyond
            // the right edge). ScrollViewer honours BringIntoView automatically
            // because the headers live inside it via the TabControl template.
            selected.BringIntoView();
        }

        // Pane follows the active project — artifacts are per-project.
        RefreshArtifactPaneForActiveTab();

        if (Tabs.SelectedItem is TabItem { Tag: SessionTab session })
        {
            ShowTabContent(session);

            // Tab is now active → clear the "new since seen" badge. The
            // unpending count (toolbar button) is untouched — only Claude
            // calling firepit_inbox_complete reduces that.
            if (_inboxWatchers.TryGetValue(session.Context.Path, out var inbox))
            {
                inbox.MarkAsSeen();
            }

            // Deferred-restore path: a tab that was created on app start but
            // not eagerly initialized lights up here, the moment the user
            // first selects it. The spinner shows via the existing
            // ShowLoadingIndicator path inside StartSessionAsync.
            //
            // Guard: during RestoreTabsFromState's loop, WPF fires spurious
            // SelectionChanged events as Tabs.Items.Add triggers layout. If
            // we honoured them we'd start non-active tabs eagerly — each one
            // queueing behind the active tab's WebView2 init (~45 s cold).
            // The restore method does an explicit StartDeferredTab call for
            // the active tab after the loop finishes.
            if (!_restoring)
            {
                StartDeferredTab(session);
            }

            // Defer focus until layout settles — calling immediately during
            // SelectionChanged races the WebView2 hwnd attach and the focus
            // call no-ops. One dispatcher tick later is enough.
            Dispatcher.BeginInvoke(new Action(session.FocusTerminal), DispatcherPriority.Input);
        }
        else
        {
            HideAllTabContent();
        }
    }

    /// <summary>
    /// Mount <paramref name="session"/>'s content (once) and make it the only
    /// visible tab content. Inactive tabs stay mounted but Collapsed so their
    /// WebView2 HwndHost — and its OLE drop registration and in-flight boot —
    /// survive the switch. Collapsed hides the child hwnd, so no airspace
    /// overlap between the visible and hidden terminals.
    /// </summary>
    private void ShowTabContent(SessionTab session)
    {
        var content = session.Content;
        if (!TabContentHost.Children.Contains(content))
        {
            TabContentHost.Children.Add(content);
        }
        foreach (UIElement child in TabContentHost.Children)
        {
            child.Visibility = ReferenceEquals(child, content) ? Visibility.Visible : Visibility.Collapsed;
        }
        TabContentHost.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    /// <summary>Detach a closed tab's content from the persistent host.</summary>
    private void UnmountTabContent(SessionTab session)
    {
        if (TabContentHost.Children.Contains(session.Content))
        {
            TabContentHost.Children.Remove(session.Content);
        }
    }

    private void HideAllTabContent()
    {
        foreach (UIElement child in TabContentHost.Children)
        {
            child.Visibility = Visibility.Collapsed;
        }
        TabContentHost.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabItem tabItem })
        {
            _ = CloseTabAsync(tabItem, confirmIfBurning: true);
        }
    }

    /// <summary>
    /// Single tab-close entry point shared by the close button, the right-click
    /// context menu, and keyboard shortcuts. When the session is Burning and
    /// confirmation is requested, prompts before killing — matches the Rekindle
    /// confirm UX, so accidentally hitting Ctrl+W on a live agent doesn't lose
    /// work.
    /// </summary>
    private async Task CloseTabAsync(TabItem tabItem, bool confirmIfBurning)
    {
        if (tabItem.Tag is not SessionTab session)
        {
            return;
        }

        if (confirmIfBurning && session.State == Core.Sessions.SessionState.Burning)
        {
            var confirmed = MessageDialog.Show(
                owner: this,
                title: "Close tab?",
                message: $"The session in {session.Context.Name} is still burning. Closing the tab kills the running agent.",
                primaryLabel: "Close",
                secondaryLabel: "Keep open");
            if (!confirmed)
            {
                return;
            }
        }

        var key = _openTabs.FirstOrDefault(kvp => ReferenceEquals(kvp.Value.TabItem, tabItem)).Key;
        if (key is not null)
        {
            _openTabs.Remove(key);
            DisposeConfigWatcher(key);
            DisposeInboxWatcher(key);
            DisposeRunsWatcher(key);
            // If this tab actually ran a session, a Claude conversation exists
            // on disk for this project. Remember it so a later reopen resumes
            // with --continue instead of starting fresh and losing context.
            if (session.IsStarted || session.PendingResume)
            {
                _resumableProjects.Add(key);
            }
        }

        var index = Tabs.Items.IndexOf(tabItem);
        Tabs.Items.Remove(tabItem);
        if (Tabs.Items.Count == 0)
        {
            Tabs.Visibility = Visibility.Collapsed;
            HideAllTabContent();
        }
        else
        {
            var newIndex = Math.Min(index, Tabs.Items.Count - 1);
            ((TabItem)Tabs.Items[newIndex]!).IsSelected = true;
        }

        UnmountTabContent(session);
        ScheduleStateSave();
        try { await session.DisposeAsync(); } catch { /* ignored */ }
    }

    private void OnTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem tab) return;
        // Skip drag-arming when the press lands on the close button (or its
        // visual descendants) — otherwise every click of × also arms a drag.
        if (e.OriginalSource is DependencyObject origin && IsInsideCloseButton(origin))
        {
            _dragSource = null;
            return;
        }
        _dragSource = tab;
        _dragStart  = e.GetPosition(Tabs);
    }

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Tabs);
        if (Math.Abs(pos.X - _dragStart.X) < DragThresholdPx
            && Math.Abs(pos.Y - _dragStart.Y) < DragThresholdPx) return;

        var source = _dragSource;
        _dragSource = null; // DoDragDrop is modal — clear before so we don't re-enter
        try
        {
            DragDrop.DoDragDrop(source, source, DragDropEffects.Move);
        }
        finally
        {
            HideDropIndicator();
        }
    }

    private void OnTabsDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TabItem)) is not TabItem source)
        {
            e.Effects = DragDropEffects.None;
            HideDropIndicator();
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(Tabs);
        var (target, insertBefore) = HitTestTab(pos);
        if (target is null || ReferenceEquals(target, source))
        {
            e.Effects = DragDropEffects.None;
            HideDropIndicator();
        }
        else
        {
            e.Effects = DragDropEffects.Move;
            ShowDropIndicatorAt(target, insertBefore);
        }
        e.Handled = true;
    }

    private void OnTabsDragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private void OnTabsDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        if (e.Data.GetData(typeof(TabItem)) is not TabItem source) return;

        var (target, insertBefore) = HitTestTab(e.GetPosition(Tabs));
        if (target is null || ReferenceEquals(target, source)) return;

        var srcIdx = Tabs.Items.IndexOf(source);
        var tgtIdx = Tabs.Items.IndexOf(target);
        if (srcIdx < 0 || tgtIdx < 0) return;

        // Compute the new index after removing source.
        var insertIdx = insertBefore ? tgtIdx : tgtIdx + 1;
        if (srcIdx < insertIdx) insertIdx--;
        if (insertIdx == srcIdx) return;

        Tabs.Items.Remove(source);
        Tabs.Items.Insert(insertIdx, source);
        source.IsSelected = true;
        ScheduleStateSave();
        e.Handled = true;
    }

    private (TabItem? Target, bool InsertBefore) HitTestTab(System.Windows.Point pos)
    {
        foreach (var item in Tabs.Items)
        {
            if (item is not TabItem tab) continue;
            var origin = tab.TransformToAncestor(Tabs).Transform(default);
            var width  = tab.ActualWidth;
            if (pos.X >= origin.X && pos.X <= origin.X + width)
            {
                // Left half → insert before this tab; right half → insert after.
                return (tab, pos.X < origin.X + width / 2.0);
            }
        }
        return (null, false);
    }

    private void ShowDropIndicatorAt(TabItem target, bool insertBefore)
    {
        var origin = target.TransformToAncestor(Tabs).Transform(default);
        var x = insertBefore ? origin.X : origin.X + target.ActualWidth;
        TabDropIndicator.Margin = new Thickness(x - 1, 0, 0, 0);
        TabDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => TabDropIndicator.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Vertical mouse wheel over the tab strip → horizontal scroll. WPF's
    /// ScrollViewer defaults to vertical-only on wheel; we explicitly map
    /// delta to the horizontal axis here so the user can spin the wheel to
    /// pan through overflowing tab headers. Step size matches one notch
    /// (≈120 px), which feels comparable to the per-line scroll of a
    /// vertical list.
    /// </summary>
    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // Negative delta = wheel toward user = scroll right.
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static bool IsInsideCloseButton(DependencyObject origin)
    {
        for (var node = origin; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button { Name: "CloseBtn" }) return true;
        }
        return false;
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        ProjectPicker.IsOpen = !ProjectPicker.IsOpen;
    }

    private void OnProjectPickerOpened(object? sender, EventArgs e)
    {
        ReloadProjectList();
        PickerSearch.Text = string.Empty;
        RefreshPickerItems(string.Empty);

        // WebView2 keeps the OS keyboard focus until something explicitly
        // pulls it away — PickerSearch.Focus() alone only moves WPF's logical
        // focus, so typed characters keep landing in the active Claude tab.
        // We have to wait for the popup's HWND to exist (Loaded fires on the
        // popup's Border) before SetFocus can do anything, hence the Loaded
        // hop instead of a same-tick Dispatcher.InvokeAsync.
        if (PickerSearch.IsLoaded)
        {
            GrabPickerFocus();
        }
        else
        {
            void OnceLoaded(object s, RoutedEventArgs ev)
            {
                PickerSearch.Loaded -= OnceLoaded;
                GrabPickerFocus();
            }
            PickerSearch.Loaded += OnceLoaded;
        }
    }

    private void GrabPickerFocus(int attempt = 0)
    {
        // Pulling OS keyboard focus out of the WebView2 sibling HWND races two
        // things: the popup's own HWND coming up (MoveOsFocusTo no-ops until it
        // exists — its own doc says "re-try on a later tick") and WebView2
        // occasionally grabbing focus back. A single shot won only
        // intermittently — and since the v0.5.46 output coalescing, the
        // terminal's Background-priority flushes compete with it too. So: post
        // at Input priority (above those flushes, below Render) AND retry across
        // a few ticks until the search box actually holds keyboard focus.
        // Bounded so a genuinely-unfocusable popup never spins.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ProjectPicker.IsOpen)
            {
                return;
            }
            var osMoved = NativeFocus.MoveOsFocusTo(PickerSearch);
            PickerSearch.Focus();
            Keyboard.Focus(PickerSearch);

            if ((!osMoved || !PickerSearch.IsKeyboardFocused) && attempt < 8)
            {
                GrabPickerFocus(attempt + 1);
            }
        }), DispatcherPriority.Input);
    }

    private void OnPickerSearchChanged(object sender, TextChangedEventArgs e)
    {
        RefreshPickerItems(PickerSearch.Text);
    }

    private void OnPickerSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MovePickerSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MovePickerSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                ActivatePickerSelection(
                    PickerList.SelectedItem as ProjectPickerItem ?? _pickerItems.FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Escape:
                ProjectPicker.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Move the picker selection while keyboard focus stays in the search box
    /// — command-palette style. Focus never enters the list, so typing keeps
    /// filtering and ↑/↓ keep walking the filtered results. (The old
    /// focus-the-container approach broke right after filtering: the
    /// ListBoxItem container isn't generated yet on the same tick, so Down
    /// only ever re-selected the first row.)
    /// </summary>
    private void MovePickerSelection(int direction)
    {
        if (_pickerItems.Count == 0)
        {
            return;
        }
        PickerList.SelectedIndex = PickerList.SelectedIndex < 0
            ? (direction > 0 ? 0 : _pickerItems.Count - 1)
            : Math.Clamp(PickerList.SelectedIndex + direction, 0, _pickerItems.Count - 1);
        PickerList.ScrollIntoView(PickerList.SelectedItem);
    }

    private void OnPickerListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ActivatePickerSelection(PickerList.SelectedItem as ProjectPickerItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ProjectPicker.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnPickerItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { Content: ProjectPickerItem item })
        {
            ActivatePickerSelection(item);
            e.Handled = true;
        }
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = _settings.ProjectsRoot,
            Title = "Pick a folder to open as a project",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OpenManualProject(dialog.FolderName);
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        var root = _settings.ProjectsRoot;
        var name = InputDialog.Show(
            this,
            title: "New project",
            message: $"Creates a new folder under:\n  {root}",
            primaryLabel: "Create",
            validate: candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return null; // empty is "not yet valid" but shows no scolding error
                }
                if (candidate.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                {
                    return "Name contains characters not allowed in a folder name.";
                }
                if (Directory.Exists(System.IO.Path.Combine(root, candidate)))
                {
                    return "A folder with that name already exists.";
                }
                return null;
            });

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var folder = System.IO.Path.Combine(root, name);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create project folder {Folder}", folder);
            ShowToast($"Could not create folder: {ex.Message}", isError: true);
            return;
        }

        OpenManualProject(folder);
    }

    /// <summary>
    /// Persists <paramref name="folder"/> as a manual project entry (so it
    /// survives restart), opens a session tab for it, and refreshes the picker.
    /// Shared by "Browse for folder…" and "New project…".
    /// </summary>
    private void OpenManualProject(string folder)
    {
        // Robust last-segment derivation — Path.GetFileName is empty for a UNC
        // share root (\\nas\music) or drive root, which left network projects
        // with a blank tab.
        var name = ProjectNaming.DeriveName(folder);

        var existing = (_settings.Projects ?? []).ToList();
        if (!existing.Any(p => string.Equals(p.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            existing.Add(new ProjectSettings(name, folder));
            _settings = _settings with { Projects = existing };
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist manual project {Folder}", folder);
                ShowToast("Could not save project to settings.json — opening anyway", isError: true);
            }
        }

        ProjectPicker.IsOpen = false;
        var project = new Project(name, folder, ClaudeCodeAdapter.AdapterId);
        OpenSessionTabInteractive(project);

        // Refresh the in-memory list so the picker shows the new entry next time.
        ReloadProjectList();
    }

    private void ActivatePickerSelection(ProjectPickerItem? item)
    {
        if (item?.Project is null)
        {
            return;
        }
        ProjectPicker.IsOpen = false;
        OpenSessionTabInteractive(item.Project);
    }

    private void RefreshPickerItems(string filter)
    {
        var goldBrush = (Brush)new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B));
        var defaultBrush = (Brush)new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD8));
        goldBrush.Freeze();
        defaultBrush.Freeze();

        _pickerItems.Clear();
        var query = string.IsNullOrWhiteSpace(filter)
            ? _allProjects
            : _allProjects.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var project in query)
        {
            var open = _openTabs.ContainsKey(project.Path);
            _pickerItems.Add(new ProjectPickerItem(
                Project: project,
                Name: project.Name,
                Path: project.Path,
                Status: open ? "open" : string.Empty,
                NameBrush: open ? goldBrush : defaultBrush));
        }
        // With an active filter, pre-select the top hit so Enter's target is
        // visible and ↑/↓ continue from it. The unfiltered list stays
        // unselected — a default there reads as clutter; Enter still opens
        // the first project via the FirstOrDefault fallback.
        PickerList.SelectedIndex =
            !string.IsNullOrWhiteSpace(filter) && _pickerItems.Count > 0 ? 0 : -1;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutDialog { Owner = this };
        Views.DialogSizing.Apply(about, AboutDialog.DesignWidth);
        about.ShowDialog();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var previousFontSize = (_settings.Ui ?? UiSettings.Defaults).ResolvedFontSize;
        var dialog = new SettingsDialog(_settingsStore) { Owner = this };
        Views.DialogSizing.Apply(dialog, SettingsDialog.DesignWidth);
        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
        {
            _settings = updated;
            _quickLinks = BuildQuickLinkService(_settings);
            _mcpRegistry = new SettingsBackedMcpRegistry(
                _settings,
                _secretResolver,
                _projectConfigStore.Load,
                warn: msg => Log.Warning("McpRegistry: {Message}", msg));
            ReloadProjectList();

            var newFontSize = (_settings.Ui ?? UiSettings.Defaults).ResolvedFontSize;
            if (newFontSize != previousFontSize)
            {
                // StaticResource lookups in XAML resolve once — the window chrome
                // stays at the old size until the process restarts. Offer a
                // self-restart so the user doesn't have to remember.
                var restartNow = Views.MessageDialog.Show(
                    this,
                    title: "Restart to apply font size?",
                    message: $"Font size set to {newFontSize}pt. Firepit needs to restart for the window chrome (tabs, captions, dialogs) to pick up the new size.",
                    primaryLabel: "Restart now",
                    secondaryLabel: "Later");
                if (restartNow)
                {
                    SelfRestart();
                }
            }
        }
    }

    private void SelfRestart()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warning("Self-restart requested but ProcessPath is empty");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            });
            // Shutdown triggers OnClosing → tabs persisted, sessions disposed.
            // The new instance picks up immediately because the singleton guard
            // releases its mutex on dispose (see OnExit).
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-restart failed");
            ShowToast($"Restart failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Debounced request to persist the tab layout. Collapses bursts (drag
    /// reorder, rapid tab switching, window dragging) into one write a second
    /// after the last change.
    /// </summary>
    private void ScheduleStateSave()
    {
        if (_restoring || !_settings.Tabs.PersistAcrossRestarts)
        {
            return;
        }
        if (_stateSaveTimer is null)
        {
            _stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _stateSaveTimer.Tick += (_, _) =>
            {
                _stateSaveTimer!.Stop();
                SaveStateSnapshot();
            };
        }
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    /// <summary>
    /// Write the current tab layout, active tab, and window placement to
    /// state.json, preserving every unrelated field (trust ledger, migration
    /// flags, artifact-pane pin). Runs debounced on layout changes, on OS
    /// session end, and finally in OnClosing — relying on OnClosing alone
    /// loses the layout whenever Windows kills the process at shutdown.
    /// </summary>
    private void SaveStateSnapshot()
    {
        try
        {
            // Iterate Tabs.Items (visual order) rather than _openTabs.Values
            // (insertion order) so drag-reordered sessions restore in the
            // user's chosen layout.
            var activeName = (Tabs.SelectedItem as TabItem)?.Tag is SessionTab activeSession
                ? activeSession.Context.Name
                : null;
            var tabs = Tabs.Items.OfType<TabItem>()
                .Where(t => t.Tag is SessionTab)
                .Select(t =>
                {
                    var s = (SessionTab)t.Tag!;
                    return new TabState(
                        ProjectName: s.Context.Name,
                        LastSessionResumable: s.State != Core.Sessions.SessionState.Dead);
                })
                .ToArray();
            _stateStore.Save(_stateStore.Load() with
            {
                Tabs = tabs,
                ActiveTabProjectName = activeName,
                Window = CaptureWindowPlacement(),
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist tab state");
        }
    }

    private void OnOsSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // OS shutdown/logoff. Save synchronously right here — past this
        // handler the process can be terminated at any moment, and WPF's
        // normal Closing pipeline is not guaranteed to run.
        _stateSaveTimer?.Stop();
        if (_settings.Tabs.PersistAcrossRestarts)
        {
            SaveStateSnapshot();
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _disposedUpdates = true;
        _updateTimer?.Stop();
        _stateSaveTimer?.Stop();

        if (_settings.Tabs.PersistAcrossRestarts)
        {
            SaveStateSnapshot();
        }

        var sessions = _openTabs.Values.Select(t => t.Session).ToArray();
        _openTabs.Clear();
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync(); } catch { /* ignored */ }
        }

        foreach (var watcher in _configWatchers.Values)
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
        _configWatchers.Clear();

        foreach (var watcher in _inboxWatchers.Values)
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
        _inboxWatchers.Clear();

        foreach (var watcher in _runsWatchers.Values)
        {
            try { watcher.Dispose(); } catch { /* ignored */ }
        }
        _runsWatchers.Clear();

        if (_jobScheduler is not null)
        {
            try { await _jobScheduler.DisposeAsync(); } catch { /* ignored */ }
            _jobScheduler = null;
        }

        DisposeKnowledgeService();
    }

    public void ShowToast(string message, bool isError = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(message, isError));
            return;
        }

        ToastText.Text = message;
        Toast.BorderBrush = new SolidColorBrush(isError
            ? Color.FromRgb(0xCD, 0x5C, 0x5C)
            : Color.FromRgb(0xF5, 0xC9, 0x7B));
        Toast.Visibility = Visibility.Visible;

        var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        hideTimer.Tick += (s, _) =>
        {
            Toast.Visibility = Visibility.Collapsed;
            ((DispatcherTimer)s!).Stop();
        };
        hideTimer.Start();
    }

    public sealed record ProjectPickerItem(
        Project Project,
        string Name,
        string Path,
        string Status,
        Brush NameBrush);
}
