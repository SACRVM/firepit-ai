namespace Firepit.Mcp;

/// <summary>
/// Surface the MCP host calls into the running Firepit GUI through.
/// MainWindow implements this. All members are async — the host always
/// awaits. Implementations are responsible for marshalling onto the WPF
/// dispatcher when they touch UI state.
/// </summary>
public interface IMcpBackend
{
    Task<IReadOnlyList<ProjectInfo>>  ListProjectsAsync();
    Task<IReadOnlyList<SessionInfo>>  ListSessionsAsync();

    /// <summary>Effective settings as a JSON object string with secret values redacted.</summary>
    Task<string>                      GetRedactedSettingsAsync();

    /// <summary>Create/register a project without a UI step or restart: ensure
    /// the folder, register it (manual entry, mirroring the UI's "New
    /// project…" flow), optionally apply the default blueprint, then reload
    /// the project list so every other tool sees it immediately.</summary>
    Task<CreateProjectResult>         CreateProjectAsync(string name, string? path, bool applyBlueprint);

    /// <summary>Atomic rename cascade: release Firepit's folder handles,
    /// rename the folder (under the projects root), migrate the Claude Code
    /// history key, update config id + settings entry, reload. Requires the
    /// project's tab to be closed. Source addressed by name (empty string
    /// allowed) or path.</summary>
    Task<RenameProjectResult>         RenameProjectAsync(string? from, string? fromPath, string to,
                                                         bool renameFolder, bool migrateHistory);

    Task<ToolCallResult>              OpenTabAsync(string projectName, bool resume);
    Task<ToolCallResult>              FocusTabAsync(string projectName);
    Task<ToolCallResult>              CloseTabAsync(string projectName);
    Task<ToolCallResult>              ReloadAsync(string projectName, bool restart);

    /// <summary>Phase 5: write an inbox file under &lt;toProject&gt;/.firepit/inbox/.</summary>
    Task<InboxWriteResult>            SendInboxAsync(string fromProject, string toProject,
                                                     string subject, string body, string priority);

    /// <summary>List pending (un-processed) inbox messages for a project — top-level
    /// .md files under &lt;projectPath&gt;/.firepit/inbox/, with frontmatter parsed.</summary>
    Task<InboxListResult>             ListInboxAsync(string projectName);

    /// <summary>Move one inbox message from &lt;projectPath&gt;/.firepit/inbox/&lt;id&gt;
    /// into the sibling processed/ subfolder. Idempotent — already-processed
    /// (or missing) files return Ok=false with a Message.</summary>
    Task<ToolCallResult>              CompleteInboxAsync(string projectName, string id);

    /// <summary>Hard-delete an inbox message file (not the processed-move).
    /// Used by the Inbox wizard's Delete action. Idempotent — missing files
    /// return Ok=true with a "nothing to delete" note. Same path-traversal
    /// guard as Complete: only bare filenames accepted.</summary>
    Task<ToolCallResult>              DeleteInboxMessageAsync(string projectName, string id);

    /// <summary>Append (or replace by name) a toolbar command in
    /// &lt;projectPath&gt;/.firepit/config.json. The handler applies the new
    /// config to the open tab itself — it must not depend on the config
    /// watcher, which only covers edits made outside Firepit and is gated
    /// behind tabs.autoReloadOnConfigChange.</summary>
    Task<ToolCallResult>              AddProjectCommandAsync(string projectName, AddCommandSpec spec);

    /// <summary>Return the current toolbar commands list for a project (empty if
    /// none configured). Read-only; lets agents discover what's already wired
    /// without parsing the JSONC file themselves.</summary>
    Task<CommandListResult>           ListProjectCommandsAsync(string projectName);

    /// <summary>Remove a toolbar command by name (case-insensitive). Idempotent —
    /// if the name doesn't exist, returns Ok with a "not found" message. Same
    /// hot-reload path as Add.</summary>
    Task<ToolCallResult>              RemoveProjectCommandAsync(string projectName, string commandName);

    /// <summary>Hybrid knowledge search. <paramref name="projectScopeName"/> is the
    /// resolved calling/target project name (null when unknown); <paramref name="scope"/>
    /// is "project" | "global" | "both". Never touches UI state — no dispatcher hop.</summary>
    Task<KnowledgeSearchResult>       SearchKnowledgeAsync(string? projectScopeName, string scope,
                                                           string query, int limit);

    /// <summary>Read one knowledge document by scope name (as carried in search
    /// hits: "global" or a project name) + relative path.</summary>
    Task<KnowledgeDocumentResult>     GetKnowledgeDocumentAsync(string scopeName, string path);

    /// <summary>Write a new markdown document into the scope's
    /// .firepit/knowledge/ folder and index it before returning. Pinned docs
    /// get `pin: true` frontmatter and land in the always-on digest.</summary>
    Task<KnowledgeDocumentResult>     AddKnowledgeDocumentAsync(string scopeName, string title,
                                                               string content, bool pinned);

    /// <summary>Replace an existing document's content and re-index it before
    /// returning. Null pinned keeps the doc's current pin state.</summary>
    Task<KnowledgeDocumentResult>     UpdateKnowledgeDocumentAsync(string scopeName, string path,
                                                                  string content, string? title,
                                                                  bool? pinned);

    /// <summary>Delete a document file and drop it from the index. Missing
    /// docs return Ok=false with a Message.</summary>
    Task<ToolCallResult>              DeleteKnowledgeDocumentAsync(string scopeName, string path);

    /// <summary>List blueprints from the meta project (seeding the built-in
    /// default first). File-only work — no dispatcher state touched beyond a
    /// settings snapshot.</summary>
    Task<BlueprintListResult>         ListBlueprintsAsync();

    /// <summary>Conformance check; null projectName sweeps all projects.</summary>
    Task<BlueprintCheckResult>        CheckBlueprintAsync(string? projectName, string blueprintName);

    /// <summary>
    /// Verifies that what Firepit maintains for a project still matches
    /// reality — the knowledge index against the documents on disk, the
    /// CLAUDE.md fragment imports against the repository's current visibility,
    /// blueprint conformance. With <paramref name="repair"/>, fixes what is
    /// derived and can be rebuilt; never edits a document or a CLAUDE.md.
    /// </summary>
    Task<IntegrityCheckResult>        CheckIntegrityAsync(string? projectName, bool repair);

    /// <summary>Idempotent apply of a blueprint to one project.</summary>
    Task<BlueprintApplyResult>        ApplyBlueprintAsync(string projectName, string blueprintName,
                                                          bool fixBlanketIgnores);

    /// <summary>Link a file into the project's artifact pane
    /// (&lt;projectPath&gt;/.firepit/artifacts.json). Upsert by resolved path:
    /// re-linking the same file updates its label/note in place. The pane
    /// refreshes immediately — same rule as the command tools, no watcher
    /// dependency.</summary>
    Task<ToolCallResult>              AddArtifactAsync(string projectName, string path,
                                                       string? label, string? note);

    /// <summary>Drop an artifact link by path or label. The file itself is
    /// never touched. Idempotent — an unknown target returns Ok with a
    /// "nothing to remove" note.</summary>
    Task<ToolCallResult>              RemoveArtifactAsync(string projectName, string? path, string? label);

    /// <summary>List the project's artifact links, including ones whose target
    /// no longer exists (Exists=false) so they can be cleaned up.</summary>
    Task<ArtifactListResult>          ListArtifactsAsync(string projectName);
}
