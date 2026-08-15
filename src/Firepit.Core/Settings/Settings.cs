using System.IO;

namespace Firepit.Core.Settings;

public sealed record FirepitSettings(
    string ProjectsRoot,
    string DefaultAgent,
    string Theme,
    TabSettings Tabs,
    ShellsSettings Shells,
    TerminalThemeSettings? Terminal = null,
    UiSettings? Ui = null,
    IReadOnlyDictionary<string, McpServerSettings>? McpServers = null,
    IReadOnlyList<QuickLinkSettings>? QuickLinks = null,
    IReadOnlyList<ProjectSettings>? Projects = null,
    PlatformSettings? Platform = null,
    UpdateSettings? Updates = null)
{
    public static readonly FirepitSettings Defaults = new(
        ProjectsRoot: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SynologyDrive", "PROJECTS"),
        DefaultAgent: "claude",
        Theme: "dark",
        Tabs: TabSettings.Defaults,
        Shells: ShellsSettings.Defaults,
        Terminal: TerminalThemeSettings.Defaults,
        Ui: UiSettings.Defaults,
        Platform: PlatformSettings.Defaults,
        Updates: UpdateSettings.Defaults);
    // QuickLinks are intentionally empty by default — they're user-specific
    // (GitHub org/user, internal tools, personal dashboards). The per-project
    // .firepit/config.json scaffold shows commented examples of how to add
    // them. settings.json globals also work; configure via Settings dialog.
}

/// <summary>
/// V0.5.0+ flags grouped under the "platform" key. Kept separate from
/// existing nested records so the legacy schema stays clean.
/// </summary>
public sealed record PlatformSettings(
    bool MetaProjectPromptShown = false,
    bool InboxBadgesEnabled = true,
    bool RunBadgesEnabled = true,
    RunBadgePolicy RunBadgePolicy = RunBadgePolicy.All,
    int RunRetentionDays = 30,
    /// <summary>
    /// Hand a newly arrived inbox message to the target project's agent on its
    /// own, instead of waiting for the Inbox button. Only ever delivered to a
    /// tab that is open and idle; otherwise the message stays in the inbox,
    /// which remains the durable transport. Set false to go back to delivering
    /// by hand.
    /// </summary>
    bool InboxAutoDeliverEnabled = true)
{
    public static readonly PlatformSettings Defaults = new();
}

/// <summary>
/// What the runs badge on a tab counts. Global default in <see cref="PlatformSettings.RunBadgePolicy"/>,
/// optionally overridden per-project via <c>.firepit/config.json</c> →
/// <c>runs.badgePolicy</c>.
/// <list type="bullet">
///   <item><c>All</c> — every run since last viewed (default)</item>
///   <item><c>FailuresOnly</c> — only failure/timeout runs surface as badge</item>
/// </list>
/// </summary>
public enum RunBadgePolicy
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("all")]          All,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("failuresOnly")] FailuresOnly,
}

/// <summary>
/// One knob — UI chrome and the embedded terminal font scale together.
/// Range clamp [10, 22] is enforced in <see cref="ResolvedFontSize"/>.
/// </summary>
public sealed record UiSettings(int FontSize)
{
    public const int MinFontSize = 10;
    public const int MaxFontSize = 22;
    public const int DefaultFontSize = 12;

    public static readonly UiSettings Defaults = new(DefaultFontSize);

    // Computed property — must NOT be serialized. Without [JsonIgnore] the
    // source-gen serializer treats it as a public read-only property worth
    // writing, and settings.json ends up with a redundant "resolvedFontSize"
    // field that confuses round-tripping.
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedFontSize => Math.Clamp(FontSize, MinFontSize, MaxFontSize);
}

/// <summary>
/// Hand-edit terminal palette tokens in settings.json. Null fields fall back
/// to the brand-warm defaults. Hex strings — short or long form, both work.
/// </summary>
public sealed record TerminalThemeSettings(
    string? Background = null,
    string? Foreground = null,
    string? Cursor = null,
    string? SelectionBackground = null,
    string? SelectionForeground = null,
    string? SelectionInactiveBackground = null)
{
    public static readonly TerminalThemeSettings Defaults = new(
        Background:                  "#1A1612",
        Foreground:                  "#E8E2D8",
        Cursor:                      "#E8E2D8",
        SelectionBackground:         "#7A6855",
        SelectionForeground:         "#15110D",
        SelectionInactiveBackground: "#3A3026");

    public TerminalThemeSettings Resolved() => new(
        Background:                  Background                  ?? Defaults.Background,
        Foreground:                  Foreground                  ?? Defaults.Foreground,
        Cursor:                      Cursor                      ?? Defaults.Cursor,
        SelectionBackground:         SelectionBackground         ?? Defaults.SelectionBackground,
        SelectionForeground:         SelectionForeground         ?? Defaults.SelectionForeground,
        SelectionInactiveBackground: SelectionInactiveBackground ?? Defaults.SelectionInactiveBackground);
}

public sealed record TabSettings(
    bool PersistAcrossRestarts,
    int ActivityIdleThresholdMs,
    // Phase 2: when true, FileSystemWatcher re-applies <project>/.firepit/config.json
    // edits live. Default flipped to true in v0.5.6 after field-testing the
    // swap-file behaviour through v0.5.0–v0.5.5. The explicit firepit_reload MCP
    // tool remains available for agents that want to force a reload.
    bool AutoReloadOnConfigChange = true)
{
    public static readonly TabSettings Defaults = new(
        PersistAcrossRestarts: true,
        ActivityIdleThresholdMs: 1500,
        AutoReloadOnConfigChange: true);
}

/// <summary>
/// External program preferences. <see cref="Preferred"/> picks the external
/// shell (<c>wt</c> → Windows Terminal, PowerShell fallback).
/// <see cref="Editor"/> is what the toolbar's Editor button runs with the
/// project path as its argument — any editor CLI works (<c>code</c>,
/// <c>subl</c>, <c>devenv</c>, or a full exe path); default is VS Code's shim.
/// </summary>
public sealed record ShellsSettings(string Preferred, string Editor = "code")
{
    public static readonly ShellsSettings Defaults = new(Preferred: "wt", Editor: "code");
}

/// <summary>
/// Background update-check config (grouped under the "updates" key). Firepit
/// polls GitHub Releases for a newer version and surfaces a badge in the
/// caption bar. <see cref="IgnoredVersion"/> is the version the user chose to
/// dismiss ("Ignore this version") — the badge stays hidden until a
/// strictly higher version appears. Set <see cref="CheckForUpdates"/> to false
/// to opt out entirely.
/// </summary>
public sealed record UpdateSettings(
    bool CheckForUpdates = true,
    int CheckIntervalHours = 4,
    string? IgnoredVersion = null)
{
    public static readonly UpdateSettings Defaults = new();
}

public sealed record ProjectSettings(
    string Name,
    string Path,
    string? AgentCommand = null,
    IReadOnlyList<string>? AgentArgs = null,
    IReadOnlyList<string>? McpServers = null,
    IReadOnlyDictionary<string, McpOverrideSettings>? McpOverrides = null,
    IReadOnlyList<QuickLinkSettings>? QuickLinks = null);

public sealed record QuickLinkSettings(
    string Name,
    string Url,
    QuickLinkTargetSetting Target = QuickLinkTargetSetting.External,
    string? Icon = null,
    bool? Disabled = null);

public enum QuickLinkTargetSetting
{
    External,
    SubTab,
}

public sealed record McpServerSettings(
    string DisplayName,
    string Transport,
    string? Description = null,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? Url = null,
    IReadOnlyDictionary<string, string?>? Headers = null);

public sealed record McpOverrideSettings(
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    IReadOnlyDictionary<string, string?>? Headers = null);
