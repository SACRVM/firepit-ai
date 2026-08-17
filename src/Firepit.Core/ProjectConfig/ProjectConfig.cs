using System.Text.Json.Serialization;
using Firepit.Core.Settings;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// Per-project Firepit config. Lives at <c>&lt;projectPath&gt;/.firepit/config.json</c>.
/// Travels with the repo; the user decides per-section what to gitignore.
///
/// Resolution at session start:
///   <list type="number">
///     <item>FirepitSettings.Defaults</item>
///     <item>Global %APPDATA%\Firepit\settings.json</item>
///     <item>This file (wins on conflict for the four sections)</item>
///   </list>
/// </summary>
public sealed record ProjectConfig(
    int Version = 1,
    string? Id = null,
    IReadOnlyList<ProjectQuickLink>? QuickLinks = null,
    IReadOnlyList<ProjectMcpActivation>? McpActivations = null,
    ProjectAgentConfig? Agent = null,
    ProjectSessionConfig? Session = null,
    IReadOnlyList<ProjectCommand>? Commands = null,
    IReadOnlyList<ProjectScheduledJob>? ScheduledJobs = null,
    ProjectRunsConfig? Runs = null);

// Where a project's knowledge lives is deliberately NOT configured here. It is
// the filesystem: `.firepit/knowledge` is either a directory holding the docs
// or a file holding the path to one. A config key would need a value space,
// a value space needs reserved words, and reserved words collide with project
// names. See Firepit.Knowledge.KnowledgeLocator.

/// <summary>
/// User-defined toolbar buttons. Three variants:
/// <list type="bullet">
///   <item><c>type=shell</c>: spawn a process with <c>Command</c> + <c>Args</c> in the project dir</item>
///   <item><c>type=claude-prompt</c>: inject <c>Prompt</c> into the active session's PTY as if the user typed it</item>
///   <item><c>type=url</c>: open <c>Url</c> in the default browser (placeholder substitution applies)</item>
/// </list>
/// </summary>
public sealed record ProjectCommand(
    string Name,
    ProjectCommandType Type,
    string? Icon = null,
    // Type-specific fields. Only the ones relevant to Type matter; others ignored.
    string? Command = null,                // shell
    IReadOnlyList<string>? Args = null,    // shell
    string? Prompt = null,                 // claude-prompt
    string? Url = null,                    // url
    bool? Disabled = null,
    // v0.5.17 (issue #11 Phase A) — Shell-only knobs that the bumblebeee
    // capture-on workflow + similar dev loops actually need.
    string? Cwd = null,                    // shell — relative to project root, default = project root
    IReadOnlyDictionary<string, string?>? Env = null, // shell — merged onto the spawn env
    bool? Elevated = null,                 // shell — Windows: Verb=runas (UAC); ignored elsewhere
    bool? Confirm = null,                  // shell — modal confirm before running (state-changing ops)
    // v0.5.18 (issue #11 Phase B) — Window placement + lifecycle tracking.
    //   "new"          → spawn a fresh OS console window each click (default,
    //                    matches Phase A behaviour).
    //   "reuse:<id>"   → if a previous launch with this id is still alive,
    //                    bring its window to the foreground; otherwise spawn
    //                    and register under that id. <id> is project-scoped.
    //   "inline"       → write the resolved command line into the active
    //                    session's PTY stdin, so the agent (or shell) inside
    //                    the tab executes it. Cwd/Env/Elevated are ignored
    //                    in this mode — the PTY's environment wins.
    string? Window = null,
    // When true, the toolbar button renders a running-indicator while the
    // child process is alive and exposes a "Stop" right-click action that
    // kills the process tree. Independent of Window — typically combined
    // with "reuse:<id>" for long-running watchers (npm run dev, relay proxy)
    // so a second click focuses the existing window instead of spawning a
    // duplicate.
    bool? LongRunning = null,
    // When true, the spawned console closes on success but stays open (pauses)
    // on a non-zero exit so the user can read the error — instead of every
    // config file hand-rolling blanket "-NoExit" / "; pause" boilerplate.
    // Windowed shell only; ignored for window:"inline" (the PTY owns that).
    bool? KeepOpenOnError = null,
    // Opt-in toolbar grouping. Commands that share a Group label collapse into
    // a single dropdown button (label = the group) instead of N buttons — for
    // multi-target projects (Build & Run / Debug / Release). Purely opt-in: a
    // command with no Group renders as its own button exactly as before, so
    // genuine multi-command projects are never auto-collapsed. A group with a
    // single member also renders as a plain button (a dropdown of one is noise).
    string? Group = null);

public enum ProjectCommandType
{
    Shell,
    ClaudePrompt,
    Url,
}

public sealed record ProjectQuickLink(
    string Name,
    string Url,
    QuickLinkTargetSetting Target = QuickLinkTargetSetting.External,
    string? Icon = null,
    bool? Disabled = null);

public sealed record ProjectMcpActivation(
    string Id,
    IReadOnlyList<string>? ArgOverrides = null,
    IReadOnlyDictionary<string, string?>? EnvOverrides = null,
    IReadOnlyDictionary<string, string?>? HeaderOverrides = null);

public sealed record ProjectAgentConfig(
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string?>? EnvOverrides = null);

public sealed record ProjectSessionConfig(
    IReadOnlyDictionary<string, string?>? EnvOverrides = null);

/// <summary>
/// A scheduled headless Claude run. Fires on its cron schedule, spawns
/// <c>claude -p "&lt;prompt&gt;" --output-format json</c> in the project dir,
/// captures stdout/stderr, persists a run record under
/// <c>.firepit/runs/&lt;name&gt;/&lt;utc&gt;.json</c>. Auth comes from the user's
/// existing <c>~/.claude/</c> credentials — Firepit injects no API key.
///
/// Jobs run only while Firepit is open. Catch-up on launch fires the
/// last missed occurrence (not the full backlog).
/// </summary>
public sealed record ProjectScheduledJob(
    string Name,
    string Prompt,
    string Schedule,
    bool? Enabled = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? AllowedTools = null,
    int? MaxTurns = null,
    decimal? MaxBudgetUsd = null,
    bool? SkipPermissions = null,
    string? Timezone = null,
    JobConcurrencyPolicy? OnConcurrent = null,
    JobNotifyPolicy? Notify = null);

public enum JobConcurrencyPolicy
{
    [JsonStringEnumMemberName("skip")]           Skip,
    [JsonStringEnumMemberName("queue")]          Queue,
    [JsonStringEnumMemberName("killAndRestart")] KillAndRestart,
}

public enum JobNotifyPolicy
{
    [JsonStringEnumMemberName("always")]   Always,
    [JsonStringEnumMemberName("onChange")] OnChange,
    [JsonStringEnumMemberName("never")]    Never,
}

/// <summary>
/// Per-project overrides for the runs feature. Null fields inherit from
/// <see cref="Firepit.Core.Settings.PlatformSettings"/>.
/// </summary>
public sealed record ProjectRunsConfig(
    RunBadgePolicy? BadgePolicy = null,
    int? RetentionDays = null);
