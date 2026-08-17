using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Firepit.Core.Artifacts;
using Firepit.Core.Inbox;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Settings;
using Firepit.Mcp;
using Firepit.Views;
using Serilog;

namespace Firepit;

/// <summary>
/// IMcpBackend implementation for MainWindow. Every member marshals onto
/// the WPF dispatcher when it touches UI state. The MCP host calls these
/// from background pipe threads.
/// </summary>
public partial class MainWindow : IMcpBackend
{
    private static readonly Regex SecretKeyRegex = new(
        @"(?i)(key|token|secret|password|api[_-]?key)$",
        RegexOptions.Compiled);

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync() =>
        OnDispatcherAsync(() =>
        {
            var openByPath = _openTabs.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Session,
                StringComparer.OrdinalIgnoreCase);
            return (IReadOnlyList<ProjectInfo>)_allProjects.Select(p =>
            {
                openByPath.TryGetValue(p.Path, out var session);
                return new ProjectInfo(
                    Name:         p.Name,
                    Path:         p.Path,
                    AdapterId:    p.AdapterId,
                    IsOpen:       session is not null,
                    SessionState: session?.State.ToString());
            }).ToArray();
        });

    public Task<CreateProjectResult> CreateProjectAsync(string name, string? path, bool applyBlueprint) =>
        OnDispatcherAsync(() =>
        {
            try
            {
                name = name.Trim();
                if (name.Length == 0)
                    return new CreateProjectResult(false, "Project name is empty.");
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return new CreateProjectResult(false,
                        "Project name contains characters not allowed in a folder name.");

                var root = _settings.ProjectsRoot;
                var resolved = Path.GetFullPath(string.IsNullOrWhiteSpace(path)
                    ? Path.Combine(root, name)
                    : Path.IsPathRooted(path) ? path : Path.Combine(root, path));

                var byPath = _allProjects.FirstOrDefault(p => string.Equals(
                    Path.GetFullPath(p.Path), resolved, StringComparison.OrdinalIgnoreCase));
                var byName = FindProjectByName(name);
                if (byPath is null && byName is not null)
                {
                    return new CreateProjectResult(false,
                        $"A project named '{byName.Name}' already exists at {byName.Path} — " +
                        "pick another name or pass that path explicitly.");
                }

                var alreadyRegistered = byPath is not null;
                Directory.CreateDirectory(resolved);

                var warnings = new List<string>();
                IReadOnlyList<string>? actions = null;
                if (applyBlueprint)
                {
                    // Same operation firepit_blueprint_apply runs — idempotent,
                    // so re-running on an existing project is safe. Without a
                    // meta project there is no blueprint catalogue; fall back
                    // to the plain first-scaffold hardening (same conventions).
                    var store = new Firepit.Core.Blueprints.BlueprintStore(root);
                    Firepit.Core.Blueprints.Blueprint? blueprint = null;
                    if (store.MetaProjectExists)
                    {
                        store.EnsureDefaults();
                        blueprint = store.TryLoad(
                            Firepit.Core.Blueprints.FirepitBlueprintDefaults.DefaultBlueprintName);
                    }

                    if (blueprint is not null)
                    {
                        var outcome = Firepit.Core.Blueprints.BlueprintApplier.Apply(
                            blueprint, resolved, byPath?.Name ?? name,
                            metaProjectPath: store.MetaProjectPath);
                        actions = outcome.Actions;
                        warnings.AddRange(outcome.Warnings);
                    }
                    else
                    {
                        // Only when the central repo is actually set up —
                        // seeding fragments would otherwise create it as a side
                        // effect of scaffolding an unrelated project.
                        var scaffold = ProjectScaffolding.EnsureProjectScaffold(
                            resolved, byPath?.Name ?? name,
                            store.MetaProjectExists ? store.MetaProjectPath : null);
                        warnings.AddRange(scaffold.BlanketIgnores
                            .Select(l => $"blanket ignore '{l}' hides shared config"));
                    }
                }

                if (!alreadyRegistered)
                {
                    // Mirror the UI's "New project…" flow: a persisted manual
                    // entry, so registration survives restarts and doesn't
                    // depend on the folder qualifying for discovery.
                    var entries = (_settings.Projects ?? []).ToList();
                    entries.Add(new ProjectSettings(name, resolved));
                    _settings = _settings with { Projects = entries };
                    try
                    {
                        _settingsStore.Save(_settings);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "create_project: failed to persist settings.json");
                        warnings.Add(
                            "Could not save settings.json — the registration lives in memory " +
                            "until the next successful settings save.");
                    }
                }

                // Ends with SyncKnowledgeScopes, so the knowledge scope is
                // live before this returns — knowledge_add works immediately.
                ReloadProjectList();

                var project = _allProjects.FirstOrDefault(p => string.Equals(
                    Path.GetFullPath(p.Path), resolved, StringComparison.OrdinalIgnoreCase));
                if (project is null)
                {
                    return new CreateProjectResult(false,
                        $"Registered {resolved}, but it did not appear in the project list — see the log.");
                }

                Log.Information(
                    "MCP create_project: '{Name}' at {Path} (alreadyRegistered={Already}, blueprint={Blueprint})",
                    project.Name, project.Path, alreadyRegistered, applyBlueprint);
                var message = alreadyRegistered
                    ? $"'{project.Name}' was already registered" +
                      (applyBlueprint ? " — blueprint re-applied (idempotent)." : " — nothing to do.")
                    : $"Created and registered '{project.Name}'. firepit_open_tab starts a session when ready.";
                return new CreateProjectResult(
                    true, message, project.Name, project.Path, alreadyRegistered,
                    actions, warnings.Count > 0 ? warnings : null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "firepit_create_project failed");
                return new CreateProjectResult(false, ex.Message);
            }
        });

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync() =>
        OnDispatcherAsync(() =>
        {
            var selected = (Tabs.SelectedItem as TabItem)?.Tag as SessionTab;
            return (IReadOnlyList<SessionInfo>)_openTabs.Values
                .Select(t => new SessionInfo(
                    ProjectName: t.Session.Context.Name,
                    ProjectPath: t.Session.Context.Path,
                    State:       t.Session.State.ToString(),
                    IsActive:    ReferenceEquals(t.Session, selected)))
                .ToArray();
        });

    public Task<string> GetRedactedSettingsAsync() =>
        OnDispatcherAsync(() =>
        {
            string raw;
            using (var ms = new MemoryStream())
            {
                JsonSerializer.Serialize(ms, _settings, FirepitJsonContext.Default.FirepitSettings);
                raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            var node = JsonNode.Parse(raw)!;
            RedactSecretsInPlace(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });

    public Task<ToolCallResult> OpenTabAsync(string projectName, bool resume) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");
            OpenSessionTab(project, resume);
            return new ToolCallResult(true, $"Opened tab for {projectName}");
        });

    public Task<ToolCallResult> FocusTabAsync(string projectName) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");
            if (!_openTabs.TryGetValue(project.Path, out var entry))
                return new ToolCallResult(false, $"{projectName} has no open tab");
            // DIAGNOSTIC (v0.5.38): selecting the tab can wake a Dead session
            // (→ restart). Log every MCP focus so a tool-driven restart storm
            // is visible in the log.
            Log.Information("MCP firepit_focus_tab → selecting {Project}", projectName);
            entry.TabItem.IsSelected = true;
            entry.Session.FocusTerminal();
            return new ToolCallResult(true, $"Focused {projectName}");
        });

    public Task<ToolCallResult> CloseTabAsync(string projectName) =>
        OnDispatcherAsync(async () =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");
            if (!_openTabs.TryGetValue(project.Path, out var entry))
                return new ToolCallResult(false, $"{projectName} has no open tab");

            _openTabs.Remove(project.Path);
            DisposeConfigWatcher(project.Path);
            DisposeInboxWatcher(project.Path);
            DisposeRunsWatcher(project.Path);
            var index = Tabs.Items.IndexOf(entry.TabItem);
            Tabs.Items.Remove(entry.TabItem);
            if (Tabs.Items.Count == 0)
            {
                Tabs.Visibility = System.Windows.Visibility.Collapsed;
                HideAllTabContent();
            }
            else
            {
                var newIndex = Math.Min(index, Tabs.Items.Count - 1);
                ((TabItem)Tabs.Items[newIndex]!).IsSelected = true;
            }
            UnmountTabContent(entry.Session);
            try { await entry.Session.DisposeAsync(); } catch { /* ignored */ }
            return new ToolCallResult(true, $"Closed {projectName}");
        });

    public Task<ToolCallResult> ReloadAsync(string projectName, bool restart) =>
        OnDispatcherAsync(async () =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");
            if (!_openTabs.TryGetValue(project.Path, out var entry))
                return new ToolCallResult(false, $"{projectName} has no open tab to reload");

            var newConfig = SafeLoadProjectConfig(project.Path);
            if (restart)
            {
                _ = entry.Session.RekindleAsync(resume: true, confirmIfBurning: false);
                return new ToolCallResult(true, $"Restarted {projectName} with new config");
            }
            await entry.Session.RefreshFromConfigAsync(newConfig);
            return new ToolCallResult(true, $"Reloaded {projectName} (quick-links applied; banner if restart needed)");
        });

    /// <summary>
    /// Push a just-written <c>.firepit/config.json</c> into the project's open
    /// tab. Firepit's own writes must not route their UI refresh through the
    /// file system: <see cref="Firepit.ProjectConfig.FileSystemProjectConfigWatcher"/>
    /// exists for edits made *outside* Firepit (VS Code, Notepad, an agent's
    /// Write tool) and is gated behind <c>tabs.autoReloadOnConfigChange</c> —
    /// a setting about foreign edits that has no business deciding whether an
    /// MCP call becomes visible. Waiting for a debounced FS event to re-read
    /// and re-parse data we are already holding is pure detour; when the flag
    /// is off it never arrives at all and the tool reports a success the user
    /// never sees.
    /// </summary>
    private void ApplyConfigToOpenTab(string projectPath, Firepit.Core.ProjectConfig.ProjectConfig config)
    {
        if (!_openTabs.TryGetValue(projectPath, out var entry))
        {
            return;
        }
        // Same two calls the watcher would eventually make — just immediate.
        // Re-running them when a watcher event does land is harmless: the
        // toolbar is rebuilt from the same config and the restart banner
        // compares against the config we already applied.
        _ = entry.Session.RefreshFromConfigAsync(config);
        _jobScheduler?.InvalidateProject(projectPath);
    }

    public Task<InboxListResult> ListInboxAsync(string projectName) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null)
            {
                return new InboxListResult(
                    projectName, Array.Empty<InboxMessage>(), UnknownProject(projectName));
            }

            var inboxDir = Path.Combine(project.Path, ".firepit", "inbox");
            if (!Directory.Exists(inboxDir))
            {
                return new InboxListResult(project.Name, Array.Empty<InboxMessage>());
            }

            var messages = new List<InboxMessage>();
            foreach (var file in Directory.EnumerateFiles(inboxDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var parsed = InboxFrontmatterParser.Parse(raw);
                    messages.Add(new InboxMessage(
                        Id:       Path.GetFileName(file),
                        From:     parsed.Frontmatter.GetValueOrDefault("from"),
                        Subject:  parsed.Frontmatter.GetValueOrDefault("subject"),
                        Priority: parsed.Frontmatter.GetValueOrDefault("priority"),
                        Date:     parsed.Frontmatter.GetValueOrDefault("date"),
                        Body:     parsed.Body));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Inbox list: couldn't read {File}", file);
                }
            }
            // Stable order — by filename, which starts with an ISO date in
            // the firepit-mcp send_to convention, so newest sorts naturally.
            messages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return new InboxListResult(project.Name, messages);
        });

    public Task<ToolCallResult> CompleteInboxAsync(string projectName, string id) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");

            // Guard against path traversal in the id — only accept a bare filename.
            if (id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal))
            {
                return new ToolCallResult(false, $"Invalid id (must be a bare filename): {id}");
            }

            var inboxDir     = Path.Combine(project.Path, ".firepit", "inbox");
            var processedDir = Path.Combine(inboxDir, "processed");
            var source       = Path.Combine(inboxDir, id);
            var target       = Path.Combine(processedDir, id);

            if (!File.Exists(source))
            {
                return new ToolCallResult(false, $"No such message: {id}");
            }

            try
            {
                Directory.CreateDirectory(processedDir);
                // If a same-named processed file already exists (rare — same id
                // re-sent), append a -<timestamp> suffix rather than overwrite.
                if (File.Exists(target))
                {
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
                    var ext   = Path.GetExtension(id);
                    var stem  = Path.GetFileNameWithoutExtension(id);
                    target    = Path.Combine(processedDir, $"{stem}-{stamp}{ext}");
                }
                File.Move(source, target);
                Log.Information("Inbox: completed {Id} for {Project} → {Target}", id, project.Name, target);
                return new ToolCallResult(true, $"Moved to processed: {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Inbox complete failed for {Id} in {Project}", id, project.Name);
                return new ToolCallResult(false, ex.Message);
            }
        });

    /// <summary>
    /// Tiny YAML-frontmatter parser. Accepts the firepit_send_to format —
    /// <c>---\nkey: value\nkey: value\n---\n\nbody</c> — plus the looser legacy
    /// shape (no frontmatter at all). Values are trimmed; nested structures
    /// aren't supported because Firepit only emits flat key/value pairs.
    /// </summary>
    public Task<ToolCallResult> DeleteInboxMessageAsync(string projectName, string id) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");

            if (id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal))
            {
                return new ToolCallResult(false, $"Invalid id (must be a bare filename): {id}");
            }

            var source = Path.Combine(project.Path, ".firepit", "inbox", id);
            if (!File.Exists(source))
            {
                return new ToolCallResult(true, $"No such message: {id} (nothing to delete)");
            }

            try
            {
                File.Delete(source);
                Log.Information("Inbox: deleted {Id} from {Project}", id, project.Name);
                return new ToolCallResult(true, $"Deleted {id}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Inbox delete failed for {Id} in {Project}", id, project.Name);
                return new ToolCallResult(false, ex.Message);
            }
        });

    public Task<ToolCallResult> AddProjectCommandAsync(string projectName, AddCommandSpec spec) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");

            if (string.IsNullOrWhiteSpace(spec.Name))
                return new ToolCallResult(false, "command 'name' is required and cannot be empty");

            ProjectCommandType type;
            switch ((spec.Type ?? string.Empty).ToLowerInvariant())
            {
                case "shell":
                    if (string.IsNullOrWhiteSpace(spec.Command))
                        return new ToolCallResult(false, "shell commands require 'command'");
                    type = ProjectCommandType.Shell;
                    break;
                case "claude-prompt":
                    if (string.IsNullOrWhiteSpace(spec.Prompt))
                        return new ToolCallResult(false, "claude-prompt commands require 'prompt'");
                    type = ProjectCommandType.ClaudePrompt;
                    break;
                case "url":
                    if (string.IsNullOrWhiteSpace(spec.Url))
                        return new ToolCallResult(false, "url commands require 'url'");
                    type = ProjectCommandType.Url;
                    break;
                default:
                    return new ToolCallResult(false,
                        $"Unknown command type '{spec.Type}'. Expected: shell | claude-prompt | url");
            }

            // Existing config — or scaffold one. EnsureProjectScaffold no-ops
            // if the file already exists; on first scaffold it also drops the
            // commented tour AND sets up git hygiene + the inbox convention.
            var scaffold = ProjectScaffolding.EnsureProjectScaffold(
                project.Path, project.Name,
                Directory.Exists(MetaProjectPath) ? MetaProjectPath : null);
            if (scaffold.ScaffoldCreated)
            {
                Log.Information(
                    "First scaffold for {Project} (via add_command): gitignoreUpdated={Git}, claudeSeeded={Claude}, blanketIgnores=[{Blanket}]",
                    project.Name, scaffold.GitignoreUpdated, scaffold.ClaudeMdSeeded, string.Join(", ", scaffold.BlanketIgnores));
                if (scaffold.BlanketIgnores.Count > 0)
                {
                    ShowToast(
                        $"{project.Name}: .gitignore hides shared config ({string.Join(", ", scaffold.BlanketIgnores)}) — open Configure to fix",
                        isError: true);
                }
            }
            var existing = SafeLoadProjectConfig(project.Path)
                           ?? new Firepit.Core.ProjectConfig.ProjectConfig();

            var newCommand = new ProjectCommand(
                Name:        spec.Name,
                Type:        type,
                Icon:        spec.Icon,
                Command:     spec.Command,
                Args:        spec.Args,
                Prompt:      spec.Prompt,
                Url:         spec.Url,
                Cwd:         spec.Cwd,
                Env:         spec.Env,
                Elevated:        spec.Elevated,
                Confirm:         spec.Confirm,
                Window:          spec.Window,
                LongRunning:     spec.LongRunning,
                KeepOpenOnError: spec.KeepOpenOnError,
                Group:           spec.Group);

            var beforeCount = existing.Commands?.Count ?? 0;
            var merged      = ProjectCommandMutator.Upsert(existing.Commands, newCommand);
            var replaced    = merged.Count == beforeCount;
            var updated     = existing with { Commands = merged };

            try
            {
                _projectConfigStore.Save(project.Path, updated);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AddProjectCommand: failed to save config for {Project}", project.Name);
                return new ToolCallResult(false, $"Could not write .firepit/config.json: {ex.Message}");
            }

            ApplyConfigToOpenTab(project.Path, updated);
            Log.Information("MCP add_command: {Action} '{Name}' ({Type}) in {Project}",
                replaced ? "replaced" : "added", spec.Name, type, project.Name);
            return new ToolCallResult(true,
                $"{(replaced ? "Updated" : "Added")} command '{spec.Name}' in {project.Name}");
        });

    public Task<CommandListResult> ListProjectCommandsAsync(string projectName) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null)
                return new CommandListResult(
                    projectName, Array.Empty<CommandSummary>(), UnknownProject(projectName));

            var config   = SafeLoadProjectConfig(project.Path);
            var commands = config?.Commands ?? Array.Empty<ProjectCommand>();
            var summaries = commands.Select(ToSummary).ToArray();
            return new CommandListResult(project.Name, summaries);
        });

    public Task<ToolCallResult> RemoveProjectCommandAsync(string projectName, string commandName) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");

            if (string.IsNullOrWhiteSpace(commandName))
                return new ToolCallResult(false, "command 'name' is required and cannot be empty");

            var existing = SafeLoadProjectConfig(project.Path);
            if (existing is null || existing.Commands is null || existing.Commands.Count == 0)
            {
                return new ToolCallResult(true, $"No command '{commandName}' to remove in {project.Name} (nothing configured)");
            }

            var (updatedCommands, removed) = ProjectCommandMutator.RemoveByName(existing.Commands, commandName);
            if (!removed)
            {
                return new ToolCallResult(true, $"No command '{commandName}' in {project.Name} (nothing to remove)");
            }

            var updated = existing with { Commands = updatedCommands };
            try
            {
                _projectConfigStore.Save(project.Path, updated);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RemoveProjectCommand: failed to save config for {Project}", project.Name);
                return new ToolCallResult(false, $"Could not write .firepit/config.json: {ex.Message}");
            }

            ApplyConfigToOpenTab(project.Path, updated);
            Log.Information("MCP remove_command: '{Name}' from {Project}", commandName, project.Name);
            return new ToolCallResult(true, $"Removed command '{commandName}' from {project.Name}");
        });

    public Task<ToolCallResult> AddArtifactAsync(string projectName, string path, string? label, string? note) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");
            if (string.IsNullOrWhiteSpace(path))
                return new ToolCallResult(false, "artifact 'path' is required and cannot be empty");

            var entry = new ArtifactEntry(
                Path:       path.Trim(),
                Label:      string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                Note:       string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                AddedAtUtc: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            IReadOnlyList<ArtifactEntry> updated;
            bool replaced;
            try
            {
                var existing = _artifactStore.Load(project.Path);
                (updated, replaced) = ArtifactMutator.Upsert(existing, entry, project.Path);
                _artifactStore.Save(project.Path, updated);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AddArtifact: failed to save artifacts for {Project}", project.Name);
                return new ToolCallResult(false, $"Could not write .firepit/artifacts.json: {ex.Message}");
            }

            RefreshArtifactPane(project.Path);
            var resolved = ArtifactResolver.Resolve(entry, project.Path);
            Log.Information("MCP artifact_add: {Action} '{Label}' ({Kind}, exists={Exists}) in {Project}",
                replaced ? "replaced" : "added", resolved.Label, resolved.Kind, resolved.Exists, project.Name);

            // A link to a file that isn't there yet is legal (the agent may be
            // about to write it) but worth saying out loud — a silently dead
            // link is the one failure mode that makes the pane untrustworthy.
            var suffix = resolved.Exists ? string.Empty : " — note: that path does not exist yet";
            return new ToolCallResult(true,
                $"{(replaced ? "Updated" : "Pinned")} artifact '{resolved.Label}' in {project.Name}{suffix}");
        });

    public Task<ToolCallResult> RemoveArtifactAsync(string projectName, string? path, string? label) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null) return new ToolCallResult(false, $"Unknown project: {projectName}");

            IReadOnlyList<ArtifactEntry> existing;
            try
            {
                existing = _artifactStore.Load(project.Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RemoveArtifact: failed to load artifacts for {Project}", project.Name);
                return new ToolCallResult(false, $"Could not read .firepit/artifacts.json: {ex.Message}");
            }

            var target = !string.IsNullOrWhiteSpace(path) ? path! : label!;
            var (updated, removed) = !string.IsNullOrWhiteSpace(path)
                ? ArtifactMutator.RemoveByPath(existing, path!, project.Path)
                : ArtifactMutator.RemoveByLabel(existing, label!, project.Path);

            if (!removed)
            {
                return new ToolCallResult(true, $"No artifact '{target}' pinned in {project.Name} (nothing to remove)");
            }

            try
            {
                _artifactStore.Save(project.Path, updated);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RemoveArtifact: failed to save artifacts for {Project}", project.Name);
                return new ToolCallResult(false, $"Could not write .firepit/artifacts.json: {ex.Message}");
            }

            RefreshArtifactPane(project.Path);
            Log.Information("MCP artifact_remove: '{Target}' from {Project}", target, project.Name);
            return new ToolCallResult(true, $"Unpinned artifact '{target}' from {project.Name} (file left untouched)");
        });

    public Task<ArtifactListResult> ListArtifactsAsync(string projectName) =>
        OnDispatcherAsync(() =>
        {
            var project = FindProjectByName(projectName);
            if (project is null)
                return new ArtifactListResult(
                    projectName, Array.Empty<ArtifactSummary>(), UnknownProject(projectName));

            IReadOnlyList<ArtifactEntry> entries;
            try
            {
                entries = _artifactStore.Load(project.Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ListArtifacts: failed to load artifacts for {Project}", project.Name);
                entries = [];
            }

            var summaries = ArtifactResolver.ResolveAll(entries, project.Path)
                .Select(a => new ArtifactSummary(
                    Path:       a.Path,
                    Label:      a.Label,
                    Kind:       a.Kind.ToString().ToLowerInvariant(),
                    Exists:     a.Exists,
                    Note:       a.Note,
                    AddedAtUtc: a.AddedAtUtc))
                .ToArray();
            return new ArtifactListResult(project.Name, summaries);
        });

    private static CommandSummary ToSummary(ProjectCommand c) => new(
        Name:        c.Name,
        Type:        c.Type switch
        {
            ProjectCommandType.Shell        => "shell",
            ProjectCommandType.ClaudePrompt => "claude-prompt",
            ProjectCommandType.Url          => "url",
            _                               => c.Type.ToString().ToLowerInvariant(),
        },
        Icon:        c.Icon,
        Command:     c.Command,
        Args:        c.Args,
        Prompt:      c.Prompt,
        Url:         c.Url,
        Cwd:         c.Cwd,
        Env:         c.Env,
        Elevated:        c.Elevated,
        Confirm:         c.Confirm,
        Window:          c.Window,
        LongRunning:     c.LongRunning,
        KeepOpenOnError: c.KeepOpenOnError,
        Group:           c.Group,
        Disabled:        c.Disabled);

    public Task<InboxWriteResult> SendInboxAsync(string fromProject, string toProject,
                                                 string subject, string body, string priority) =>
        OnDispatcherAsync(() =>
        {
            var target = FindProjectByName(toProject);
            if (target is null)
                return new InboxWriteResult(false, Message: $"Unknown target project: {toProject}");

            var inboxDir = Path.Combine(target.Path, ".firepit", "inbox");
            try
            {
                Directory.CreateDirectory(inboxDir);
                var safeFrom = SafeSlug(fromProject);
                var safeSubj = SafeSlug(subject);
                var stamp    = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
                var fileName = $"{stamp}-from-{safeFrom}-{safeSubj}.md";
                var filePath = Path.Combine(inboxDir, fileName);

                var frontmatter =
                    "---\n" +
                    $"from: {fromProject}\n" +
                    $"to: {toProject}\n" +
                    $"subject: {subject.Replace("\n", " ")}\n" +
                    $"sentAt: {DateTime.UtcNow:O}\n" +
                    $"priority: {priority}\n" +
                    "---\n\n";
                File.WriteAllText(filePath, frontmatter + body);
                Log.Information("Inbox: {From} → {To} ({Subject})", fromProject, toProject, subject);
                return new InboxWriteResult(true, Path: filePath);
            }
            catch (Exception ex)
            {
                return new InboxWriteResult(false, Message: ex.Message);
            }
        });

    // --- helpers --------------------------------------------------------

    /// <summary>
    /// Resolve a project by the name a caller uses for it — the registry name,
    /// or the <c>id</c> its own <c>.firepit/config.json</c> declares.
    ///
    /// Both are needed because they are allowed to differ. The registry keys on
    /// the folder name, while <see cref="SessionTab"/> exports
    /// <c>FIREPIT_PROJECT_NAME</c> from the configured <c>id</c> when one is
    /// set — so an agent in such a project knows itself by a name the registry
    /// has never heard of, and every tool with a projectName parameter turns
    /// into a trap for it, its own default path included. The meta project is
    /// the live example: folder <c>.firepit</c>, id <c>firepit-central</c>.
    /// </summary>
    private Firepit.Core.Projects.Project? FindProjectByName(string name)
    {
        var byName = _allProjects.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        // Second pass only on a miss: reads config files, and the registry name
        // is what nearly every caller passes.
        return _allProjects.FirstOrDefault(p =>
            SafeLoadProjectConfig(p.Path)?.Id is { Length: > 0 } id
            && string.Equals(id, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string UnknownProject(string name) =>
        $"Unknown project: '{name}'. Pass a name from firepit_list_projects, "
        + "or the id declared in that project's .firepit/config.json.";

    private static string SafeSlug(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '-' or '_') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60];
        return slug.Length == 0 ? "msg" : slug;
    }

    private static void RedactSecretsInPlace(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj.ToList())
                {
                    if (kvp.Value is null) continue;
                    if (kvp.Value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        // Always mask values that look like raw secrets by key name.
                        if (SecretKeyRegex.IsMatch(kvp.Key) && !s.StartsWith("${"))
                        {
                            obj[kvp.Key] = "***";
                        }
                        // ${cred:...} stays opaque (Claude sees the reference, not the
                        // resolved value) — that's already the on-disk shape, no change.
                    }
                    else if (kvp.Value is JsonObject or JsonArray)
                    {
                        RedactSecretsInPlace(kvp.Value);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null) RedactSecretsInPlace(item);
                }
                break;
        }
    }

    private Task<T> OnDispatcherAsync<T>(Func<T> work)
    {
        if (Dispatcher.CheckAccess()) return Task.FromResult(work());
        return Dispatcher.InvokeAsync(work).Task;
    }

    private Task<T> OnDispatcherAsync<T>(Func<Task<T>> work)
    {
        if (Dispatcher.CheckAccess()) return work();
        return Dispatcher.InvokeAsync(work).Task.Unwrap();
    }
}
