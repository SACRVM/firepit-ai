namespace Firepit.Core.State;

public sealed record AppState(
    int Version,
    IReadOnlyList<TabState> Tabs,
    // True once the v0.5.0 ProjectConfigMigrator has stripped legacy
    // per-project entries from settings.json into per-project
    // .firepit/config.json files. Old state.json files load with the
    // default (false) and the migrator runs on next launch — safe because
    // it's idempotent (skips projects already migrated).
    bool ProjectConfigMigrationDone = false,
    // Obsolete since v0.11.2: marked the one-shot v0.5.16 strip of the two
    // hardcoded default quickLinks (issue #14) as done. The strip itself is
    // gone; the flag stays so state.json files written by older builds still
    // round-trip without losing data.
    bool LegacyQuickLinksMigrationDone = false,
    // Name of the tab that was active when the app last closed. On restore,
    // this tab gets focus and is the only one whose session starts eagerly;
    // others stay cold until the user clicks them. Null = legacy/no
    // preference, fall back to last in the Tabs list (pre-v0.5.3 behaviour).
    string? ActiveTabProjectName = null,
    // Window position and size from the previous session. Null on first
    // launch and on legacy state.json files; MainWindow falls back to the
    // XAML defaults (CenterScreen, 1180x700) in that case.
    WindowPlacement? Window = null,
    // Per-project trust ledger for .firepit/config.json files that declare
    // shell-type commands[]. v0.5.17 issue #11. Entry = (projectPath,
    // contentSha256). The user is prompted the first time a project's
    // config gains shell commands, and again after any change to the file
    // (different hash → re-prompt). Until trusted, RunCommand for shell
    // type bails. URL and prompt-type commands skip the gate entirely —
    // they can't execute arbitrary local code.
    IReadOnlyList<TrustedProjectCommands>? TrustedCommands = null,
    // Artifact pane pinned open. Unpinned (default) means the pane opens only
    // when the paperclip is clicked and closes with it; pinned restores it on
    // the next launch.
    bool ArtifactPanePinned = false)
{
    public const int CurrentVersion = 1;

    public static readonly AppState Empty = new(CurrentVersion, []);
}

/// <summary>
/// One trust grant. <see cref="ProjectPath"/> is the project root absolute
/// path; <see cref="ConfigSha256"/> is the hex-encoded SHA-256 of the
/// <c>.firepit/config.json</c> bytes the user approved. Any byte-level edit
/// invalidates the trust and re-prompts on next session start / hot-reload.
/// </summary>
public sealed record TrustedProjectCommands(string ProjectPath, string ConfigSha256);

public sealed record TabState(
    string ProjectName,
    bool LastSessionResumable);

/// <summary>
/// Persisted window bounds. <see cref="Left"/> and <see cref="Top"/> are in
/// virtual-screen device-independent pixels. When the user closed the app
/// with the window maximized, <see cref="IsMaximized"/> is true and the
/// L/T/W/H values come from <c>Window.RestoreBounds</c> so the restore-down
/// rect is preserved.
/// </summary>
public sealed record WindowPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized);
