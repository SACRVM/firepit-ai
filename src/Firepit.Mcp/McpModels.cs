namespace Firepit.Mcp;

// DTOs the host serialises out as MCP results / resources. Plain records,
// camelCase via the source-gen context.

public sealed record ProjectInfo(
    string Name,
    string Path,
    string AdapterId,
    bool IsOpen,
    string? SessionState);

public sealed record SessionInfo(
    string ProjectName,
    string ProjectPath,
    string State,
    bool IsActive);

public sealed record ToolCallResult(
    bool Ok,
    string? Message = null);

public sealed record InboxWriteResult(
    bool Ok,
    string? Path = null,
    string? Message = null);

/// <summary>
/// One pending inbox message as returned by firepit_inbox_list. <see cref="Id"/>
/// is the filename (no path) — agents pass it back to firepit_inbox_complete.
/// </summary>
public sealed record InboxMessage(
    string Id,
    string? From,
    string? Subject,
    string? Priority,
    string? Date,
    string Body);

public sealed record InboxListResult(
    string Project,
    IReadOnlyList<InboxMessage> Messages,
    /// <summary>
    /// Set when the listing could not be produced at all — an unknown project,
    /// above all. An empty <c>Messages</c> must only ever mean "nothing is
    /// waiting"; a caller cannot tell a misaddressed request from a clear
    /// inbox, and an agent that believes the latter stops looking.
    /// </summary>
    string? Error = null);

/// <summary>One toolbar command as exposed by firepit_list_commands. Mirrors
/// ProjectCommand but flattens the type discriminator to a string and drops
/// fields that don't apply to the current type so agents see a clean payload.</summary>
public sealed record CommandSummary(
    string Name,
    string Type,
    string? Icon,
    string? Command,
    IReadOnlyList<string>? Args,
    string? Prompt,
    string? Url,
    string? Cwd,
    IReadOnlyDictionary<string, string?>? Env,
    bool? Elevated,
    bool? Confirm,
    string? Window,
    bool? LongRunning,
    bool? KeepOpenOnError,
    string? Group,
    bool? Disabled);

public sealed record CommandListResult(
    string Project,
    IReadOnlyList<CommandSummary> Commands,
    /// <summary>See <see cref="InboxListResult.Error"/> — empty must mean empty.</summary>
    string? Error = null);

/// <summary>
/// Payload for firepit_add_command. Mirrors ProjectCommand 1:1 but uses string
/// for the type discriminator so the wire layer doesn't carry a Core enum.
/// The handler validates type + required fields per type.
/// </summary>
public sealed record AddCommandSpec(
    string Name,
    string Type,
    string? Icon = null,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Prompt = null,
    string? Url = null,
    string? Cwd = null,
    IReadOnlyDictionary<string, string?>? Env = null,
    bool? Elevated = null,
    bool? Confirm = null,
    string? Window = null,
    bool? LongRunning = null,
    bool? KeepOpenOnError = null,
    string? Group = null);

/// <summary>One knowledge search hit. <see cref="Scope"/> is the scope name
/// ("global" or a project name) — pass it back to firepit_knowledge_get
/// together with <see cref="Path"/> to read the full document.</summary>
public sealed record KnowledgeHitInfo(
    string Scope,
    string Path,
    string Title,
    string? Heading,
    string Snippet,
    double Score);

public sealed record KnowledgeSearchResult(
    bool Ok,
    string? Message,
    IReadOnlyList<KnowledgeHitInfo> Hits,
    bool Degraded);

/// <summary>Result of firepit_knowledge_get / firepit_knowledge_add. On
/// success carries the document; on failure only Ok=false + Message.</summary>
public sealed record KnowledgeDocumentResult(
    bool Ok,
    string? Message,
    string? Scope = null,
    string? Path = null,
    string? Title = null,
    string? Content = null);

/// <summary>Result of firepit_create_project. <see cref="AlreadyRegistered"/>
/// marks the idempotent path: the folder was known before the call.</summary>
public sealed record CreateProjectResult(
    bool Ok,
    string? Message,
    string? Name = null,
    string? Path = null,
    bool AlreadyRegistered = false,
    IReadOnlyList<string>? BlueprintActions = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Result of firepit_rename_project. <see cref="Name"/>/<see cref="Path"/>
/// are the project's final identity after the cascade.</summary>
public sealed record RenameProjectResult(
    bool Ok,
    string? Message,
    string? Name = null,
    string? Path = null,
    bool FolderRenamed = false,
    bool HistoryMigrated = false,
    IReadOnlyList<string>? Warnings = null);

/// <summary>One blueprint as exposed by firepit_blueprint_list.</summary>
public sealed record BlueprintInfo(
    string Name,
    string Description,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> GitignoreLines,
    IReadOnlyList<string> ClaudeMdMarkers,
    bool EnsuresProjectConfig);

public sealed record BlueprintListResult(
    bool Ok,
    string? Message,
    IReadOnlyList<BlueprintInfo> Blueprints);

/// <summary>Conformance of one project: <see cref="Pending"/> lists the
/// actions an apply would take (empty = conformant); <see cref="Warnings"/>
/// carries blanket-ignore findings that apply won't touch unfixed.</summary>
public sealed record BlueprintProjectCheck(
    string Project,
    bool Conformant,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Warnings);

public sealed record BlueprintCheckResult(
    bool Ok,
    string? Message,
    string? Blueprint,
    IReadOnlyList<BlueprintProjectCheck> Projects);

/// <summary>
/// One project's integrity findings. Split by what a caller can safely do
/// about them: <see cref="Repairs"/> already happened and only touched derived
/// data, <see cref="Findings"/> need a decision.
/// </summary>
public sealed record ProjectIntegrity(
    string Project,
    bool Sound,
    IReadOnlyList<IntegrityFinding> Findings,
    IReadOnlyList<string> Repairs);

/// <param name="Severity">
/// "error" means an agent is being actively misled — about what it may commit,
/// or about knowledge that exists but cannot be found. "warning" is drift.
/// </param>
public sealed record IntegrityFinding(
    string Severity,
    string Area,
    string Message,
    string? Fix = null);

public sealed record IntegrityCheckResult(
    bool Ok,
    string? Message,
    IReadOnlyList<ProjectIntegrity> Projects);

public sealed record BlueprintApplyResult(
    bool Ok,
    string? Message,
    string? Project = null,
    string? Blueprint = null,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// One entry in a project's artifact pane, as reported over the wire.
/// <paramref name="Kind"/> is the lowercase classification (image, markdown,
/// text, document, executable, archive, other) so an agent can tell what it
/// linked without re-deriving it from the extension. <paramref name="Exists"/>
/// is false for a link whose target has been moved or deleted — the entry is
/// still listed so it can be cleaned up deliberately.
/// </summary>
public sealed record ArtifactSummary(
    string Path,
    string Label,
    string Kind,
    bool Exists,
    string? Note = null,
    string? AddedAtUtc = null);

public sealed record ArtifactListResult(
    string Project,
    IReadOnlyList<ArtifactSummary> Artifacts,
    /// <summary>See <see cref="InboxListResult.Error"/> — empty must mean empty.</summary>
    string? Error = null);

// Resource definition returned by resources/list. Tool definitions live as
// internal records next to the catalog (McpToolDefinitionRaw) — they carry
// the inline JSON schema string rather than a parsed JsonElement.

public sealed record McpResourceDefinition(
    string Uri,
    string Name,
    string Description,
    string MimeType);
