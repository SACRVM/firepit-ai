using Firepit.Adapters;
using Firepit.Core;
using Firepit.Core.Agents;
using Firepit.Core.Blueprints;
using Firepit.Core.Desktop;
using Firepit.Core.Jobs;
using Firepit.Core.Platform;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Projects;
using Firepit.Core.Settings;
using Firepit.Core.State;
using Firepit.Core.Time;
using Firepit.Mcp;
using Serilog;

namespace Firepit.Workspace;

/// <summary>
/// Firepit without a window: the project registry, the knowledge scopes, the
/// inbox, artifacts, project commands, blueprints, integrity, scheduled jobs —
/// and the <see cref="IMcpBackend"/> the MCP host serves.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of the WPF <c>MainWindow</c>, which was all of this plus a
/// window (docs/LINUX.md, M11). The members are the same code; what changed is
/// what keeps them from running into each other. The window serialised every
/// MCP call by marshalling it onto its UI thread. Here a gate does that job —
/// re-entrant like the dispatcher's <c>CheckAccess</c>, so a member that calls
/// another under the gate does not deadlock on itself.
/// </para>
/// <para>
/// Knowledge members stay outside the gate, as they stayed off the
/// dispatcher: <c>KnowledgeService</c> is thread-safe, and a long index pass
/// must not hold up an inbox write.
/// </para>
/// </remarks>
public sealed partial class FirepitWorkspace : IMcpBackend, IAsyncDisposable
{
    private readonly ITabHost _tabs;
    private readonly ISettingsStore _settingsStore;
    private readonly IStateStore _stateStore;
    private readonly IProjectConfigStore _projectConfigStore;
    private readonly IReadOnlyList<IAgentAdapter> _adapters;
    private readonly IProjectOpener _projectOpener;
    private readonly bool _runJobs;
    private readonly string? _knowledgeDataRoot;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly AsyncLocal<bool> InsideGate = new();

    private FirepitSettings _settings;
    private List<Project> _allProjects = [];
    private JobScheduler? _jobScheduler;

    /// <param name="tabs">The shell, or <see cref="HeadlessTabHost"/>.</param>
    /// <param name="runJobs">Start the job scheduler. One process per machine
    /// should: two would fire every job twice.</param>
    /// <param name="knowledgeDataRoot">Where the embedding model lives;
    /// <see cref="FirepitPaths.Local"/> when null. An empty string starts no
    /// knowledge service at all — for tests, which must neither touch the
    /// user's data nor download a model.</param>
    public FirepitWorkspace(
        ITabHost tabs,
        ISettingsStore? settingsStore = null,
        IStateStore? stateStore = null,
        IProjectConfigStore? projectConfigStore = null,
        bool runJobs = true,
        string? knowledgeDataRoot = null,
        IProjectOpener? projectOpener = null)
    {
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _settingsStore = settingsStore ?? new JsonSettingsStore();
        _stateStore = stateStore ?? new JsonStateStore();
        _projectConfigStore = projectConfigStore ?? new JsonProjectConfigStore();
        _adapters = [new ClaudeCodeAdapter()];
        _runJobs = runJobs;
        _knowledgeDataRoot = knowledgeDataRoot;
        _projectOpener = projectOpener ?? new ClaudeDesktopOpener();
        _settings = _settingsStore.Load();
    }

    /// <summary>The settings in force.</summary>
    public FirepitSettings Settings => _settings;

    /// <summary>The projects known right now.</summary>
    public IReadOnlyList<Project> Projects => _allProjects;

    /// <summary>The central repo. Shared by knowledge, blueprints and scaffolding.</summary>
    internal string MetaProjectPath =>
        Path.GetFullPath(Path.Combine(_settings.ProjectsRoot, ".firepit"));

    /// <summary>
    /// Loads the project list, starts knowledge and (when asked) the job
    /// scheduler. Failures in the assist features are logged, never thrown:
    /// a broken index must not take the inbox down with it.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await Serialized(() =>
        {
            RunProjectConfigMigrationIfNeeded();
            // Before the scan, so the central project is in the first list.
            SeedCentralProjectIfMissing();
            ReloadProjectList();
            _projectsFingerprint = ProjectsFingerprint();
            _settingsStamp = _loadedSettingsStamp = SettingsStamp();
            return true;
        }).ConfigureAwait(false);

        // After the project list, so the first scope sync sees all of it.
        InitializeKnowledgeService();

        if (_runJobs)
        {
            await StartJobSchedulerAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The project a working directory belongs to: the one whose folder is
    /// the directory or the closest above it. How a bridge started by Claude
    /// Code, which exports no project name, finds out where it is.
    /// </summary>
    public Project? FindProjectByPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return _allProjects
            .Select(p => (Project: p, Path: Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Path))))
            .Where(x => string.Equals(full, x.Path, PlatformText.Paths) ||
                        full.StartsWith(x.Path + Path.DirectorySeparatorChar, PlatformText.Paths))
            .OrderByDescending(x => x.Path.Length)
            .Select(x => x.Project)
            .FirstOrDefault();
    }

    /// <summary>Re-reads settings and the project list.</summary>
    public Task ReloadAsync() =>
        Serialized(() =>
        {
            _settings = _settingsStore.Load();
            ReloadProjectList();
            _projectsFingerprint = ProjectsFingerprint();
            return true;
        });

    /// <summary>
    /// Re-reads settings and the project list when either changed on disk: a
    /// repo cloned into the projects root, an entry edited into
    /// settings.json. The window rescanned whenever its project picker
    /// opened; a daemon has no such moment, so it asks on a timer. Cheap —
    /// one directory listing and one settings read.
    /// </summary>
    /// <returns>Whether anything was reloaded.</returns>
    public Task<bool> RescanIfChangedAsync() =>
        Serialized(() =>
        {
            // Records with list members never compare equal after a fresh
            // load, so the settings file's timestamp stands in for its content.
            var settingsStamp = SettingsStamp();
            if (settingsStamp != _settingsStamp)
            {
                _settings = _settingsStore.Load();
                _settingsStamp = settingsStamp;
            }

            var fingerprint = ProjectsFingerprint();
            if (fingerprint == _projectsFingerprint && settingsStamp == _loadedSettingsStamp)
            {
                return false;
            }

            ReloadProjectList();
            _projectsFingerprint = ProjectsFingerprint();
            _loadedSettingsStamp = settingsStamp;
            return true;
        });

    private string _projectsFingerprint = string.Empty;
    private DateTime _settingsStamp;
    private DateTime _loadedSettingsStamp;

    private DateTime SettingsStamp() =>
        _settingsStore is JsonSettingsStore json && File.Exists(json.SettingsPath)
            ? File.GetLastWriteTimeUtc(json.SettingsPath)
            : default;

    private string ProjectsFingerprint() => ProjectsFingerprint(_settings.ProjectsRoot);

    /// <summary>The project root's folders, and which of them look like projects.</summary>
    private static string ProjectsFingerprint(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            return string.Join('\n', Directory.EnumerateDirectories(root)
                .Order(StringComparer.Ordinal)
                .Select(d => $"{Path.GetFileName(d)}:" +
                             $"{File.Exists(Path.Combine(d, "CLAUDE.md"))}:" +
                             $"{Directory.Exists(Path.Combine(d, ".claude"))}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_jobScheduler is not null)
        {
            try { await _jobScheduler.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warning(ex, "Job scheduler did not stop cleanly"); }
            _jobScheduler = null;
        }

        DisposeKnowledgeService();
        _gate.Dispose();
    }

    // --- registry ----------------------------------------------------------

    private void ReloadProjectList()
    {
        var rawManual = (_settings.Projects ?? []).ToList();

        // Prune orphaned manual entries — a renamed or deleted project folder
        // leaves a dead registry block (the folder is gone but its parent still
        // exists). Entries whose whole parent is missing are treated as merely
        // offline and kept, so a briefly-unavailable network or cloud mount
        // doesn't silently lose projects.
        var classified = rawManual
            .Select(p => (Entry: p, Status: ProjectRegistryHygiene.Classify(p.Path)))
            .ToList();
        var orphans = classified.Where(c => c.Status == ManualEntryStatus.Orphaned).Select(c => c.Entry).ToList();
        if (orphans.Count > 0)
        {
            rawManual = classified.Where(c => c.Status != ManualEntryStatus.Orphaned).Select(c => c.Entry).ToList();
        }

        // Heal entries persisted with a blank name. A share root used to derive
        // an empty name, and that blank was written to settings.json.
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
                    _tabs.Notify(
                        $"Removed {orphans.Count} stale project entr{(orphans.Count == 1 ? "y" : "ies")} ({names}) — folder no longer exists",
                        isError: false);
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

        var manualEntries = rawManual.Select(MapManualProject).ToArray();
        var discovery = new ProjectDiscovery(_adapters);
        _allProjects = discovery.Scan(_settings.ProjectsRoot, manualEntries).ToList();

        Log.Information("Project list loaded: root={Root}, found={Count}",
            _settings.ProjectsRoot, _allProjects.Count);
        if (_allProjects.Count == 0 && !Directory.Exists(_settings.ProjectsRoot))
        {
            _tabs.Notify($"Projects root not found: {_settings.ProjectsRoot}", isError: true);
        }

        SyncKnowledgeScopes();
    }

    private static Project MapManualProject(ProjectSettings source) => new(
        // Never a nameless project: derive from the path if settings lost it.
        Name: string.IsNullOrWhiteSpace(source.Name) ? ProjectNaming.DeriveName(source.Path) : source.Name,
        Path: source.Path,
        AdapterId: ClaudeCodeAdapter.AdapterId,
        AgentCommandOverride: source.AgentCommand,
        AgentArgsOverride: source.AgentArgs);

    private ProjectConfig? SafeLoadProjectConfig(string path)
    {
        try { return _projectConfigStore.Load(path); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load .firepit/config.json for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// One-time move of legacy <c>settings.projects[]</c> blocks into each
    /// project's <c>.firepit/config.json</c>.
    /// </summary>
    private void RunProjectConfigMigrationIfNeeded()
    {
        AppState state;
        try
        {
            state = _stateStore.Load();
        }
        catch (Exception ex)
        {
            // Couldn't read state — skip migration this time, retry next start.
            Log.Debug(ex, "State unreadable; project-config migration skipped");
            return;
        }

        if (state.ProjectConfigMigrationDone)
        {
            return;
        }

        try
        {
            if (_settings.Projects is { Count: > 0 })
            {
                var migrator = new ProjectConfigMigrator(_projectConfigStore);
                var result = migrator.Migrate(_settings);
                if (result.MigratedCount > 0)
                {
                    if (_settingsStore is JsonSettingsStore json)
                    {
                        ProjectConfigMigrator.BackupSettingsFile(json.SettingsPath);
                    }

                    _settingsStore.Save(result.Settings);
                    _settings = result.Settings;
                    Log.Information("Migrated {Count} project(s) to .firepit/config.json", result.MigratedCount);
                    _tabs.Notify($"Migrated {result.MigratedCount} project(s) to .firepit/config.json", isError: false);
                }
            }

            _stateStore.Save(state with { ProjectConfigMigrationDone = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Project-config migration failed; will retry next start");
        }
    }

    /// <summary>
    /// Seeds the central project when the projects root has none. The WPF
    /// shell asked on first run; a service has no one to ask, and without the
    /// central project there is no global knowledge base, no shared fragments
    /// and no blueprints. Only at start: a central project deleted while the
    /// service runs stays deleted until the next start. A projects root that
    /// does not exist is left alone; creating it is the user's call.
    /// </summary>
    private void SeedCentralProjectIfMissing()
    {
        var root = _settings.ProjectsRoot;
        var bootstrapper = new MetaProjectBootstrapper();
        if (!Directory.Exists(root) || bootstrapper.Exists(root))
        {
            return;
        }

        try
        {
            var written = bootstrapper.Bootstrap(root);
            new BlueprintStore(root).EnsureDefaults();
            Log.Information("Seeded the central project {Path}: {Count} file(s)",
                MetaProjectPath, written.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not seed the central project under {Root}", root);
        }
    }

    // --- jobs --------------------------------------------------------------

    private async Task StartJobSchedulerAsync(CancellationToken ct)
    {
        try
        {
            var platform = _settings.Platform ?? PlatformSettings.Defaults;
            var history = new JsonJobHistoryStore(
                retention: TimeSpan.FromDays(Math.Max(1, platform.RunRetentionDays)),
                log: msg => Log.Warning("JobHistory: {Message}", msg));
            var runner = new Firepit.Process.ProcessJobRunner();
            var source = new FileSystemJobScheduleSource(
                projectsRoot: _settings.ProjectsRoot,
                configStore: _projectConfigStore,
                warn: (project, message) => Log.Warning("Job source [{Project}]: {Message}", project, message));

            _jobScheduler = new JobScheduler(
                source, runner, history, new SystemActivityClock(),
                log: msg => Log.Information("JobScheduler: {Message}", msg));
            await _jobScheduler.StartAsync(ct).ConfigureAwait(false);
            Log.Information("Job scheduler started; root={Root}", _settings.ProjectsRoot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start job scheduler");
            _jobScheduler = null;
        }
    }

    // --- serialisation -----------------------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> with the workspace to itself. Re-entrant:
    /// work already under the gate runs inline, as the dispatcher ran work
    /// that was already on its thread.
    /// </summary>
    private async Task<T> Serialized<T>(Func<T> work)
    {
        if (InsideGate.Value)
        {
            return work();
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            InsideGate.Value = true;
            return work();
        }
        finally
        {
            InsideGate.Value = false;
            _gate.Release();
        }
    }

    /// <inheritdoc cref="Serialized{T}(Func{T})"/>
    private async Task<T> Serialized<T>(Func<Task<T>> work)
    {
        if (InsideGate.Value)
        {
            return await work().ConfigureAwait(false);
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            InsideGate.Value = true;
            return await work().ConfigureAwait(false);
        }
        finally
        {
            InsideGate.Value = false;
            _gate.Release();
        }
    }
}
