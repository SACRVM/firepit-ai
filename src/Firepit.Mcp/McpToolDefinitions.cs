namespace Firepit.Mcp;

/// <summary>
/// Static catalogue of MCP tool definitions exposed by the Firepit host.
/// Schemas are inline JSON strings — small, easy to read, no extra plumbing.
/// </summary>
internal static class McpToolDefinitions
{
    public static readonly IReadOnlyList<McpToolDefinitionRaw> All =
    [
        new("firepit_list_projects",
            "List every project Firepit knows about (discovered + manual entries). " +
            "Returns name, path, adapter id, and whether the project currently has an open tab.",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """),

        new("firepit_create_project",
            "Create/register a project with Firepit — the agent-driven 'new project' flow, no UI " +
            "step, no restart. Ensures the folder exists (default {projectsRoot}/{name} when path " +
            "is omitted), registers it in the project list immediately, and by default applies the " +
            "'firepit' blueprint so .firepit/config.json, git hygiene and the CLAUDE.md " +
            "conventions are scaffolded in the same call (falls back to the plain first-scaffold " +
            "hardening when no .firepit meta project exists). Right after this returns, " +
            "firepit_open_tab / firepit_knowledge_add {scope} / firepit_blueprint_* all work " +
            "against the new project. Idempotent: an already-registered path returns ok with a " +
            "note (and still re-applies the blueprint when requested).",
            """
            {
              "type": "object",
              "properties": {
                "name":           { "type": "string", "description": "Project name — also the folder name under the projects root when 'path' is omitted." },
                "path":           { "type": "string", "description": "Folder to register; absolute, or relative to the projects root. Created if missing. Omit for {projectsRoot}/{name}." },
                "applyBlueprint": { "type": "boolean", "description": "Scaffold .firepit/, gitignore and CLAUDE.md conventions in the same call.", "default": true }
              },
              "required": ["name"],
              "additionalProperties": false
            }
            """),

        new("firepit_rename_project",
            "Rename a project — the whole cascade in one call, no app restart: Firepit releases " +
            "its own handles on the folder (knowledge watcher + index connection pool), renames " +
            "the folder (for projects directly under the projects root; registered folders " +
            "elsewhere only change their display name), migrates the Claude Code history key " +
            "under ~/.claude/projects so chat history and auto-memory don't orphan, updates " +
            ".firepit/config.json id plus the settings.json registry entry, and reloads the " +
            "project list. The project's tab must be CLOSED first (firepit_close_tab) — the " +
            "running agent process itself holds the folder as its working directory, which no " +
            "one can rename around. If the history migration fails the folder rename is rolled " +
            "back; later metadata steps degrade to warnings instead of rolling back a successful " +
            "rename. Address the source by 'from' (name; empty string is valid) or by 'fromPath' " +
            "when the name is blank or ambiguous.",
            """
            {
              "type": "object",
              "properties": {
                "from":           { "type": "string", "description": "Current project name (may be the empty string). Ignored when fromPath is given." },
                "fromPath":       { "type": "string", "description": "Current project path — alternative addressing for blank or ambiguous names." },
                "to":             { "type": "string", "description": "New project name; also the new folder name when the folder is renamed." },
                "renameFolder":   { "type": "boolean", "description": "Rename the folder itself. Applies to folders directly under the projects root; elsewhere only the registered name changes.", "default": true },
                "migrateHistory": { "type": "boolean", "description": "Move ~/.claude/projects/<old-key> to the new key so history + auto-memory survive.", "default": true }
              },
              "required": ["to"],
              "additionalProperties": false
            }
            """),

        new("firepit_open_tab",
            "Open a tab for a project (or focus the existing one if it's already open). " +
            "Optionally pass resume=true to launch with --continue.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Name of the project to open." },
                "resume":      { "type": "boolean", "description": "Use --continue to resume the last session.", "default": false }
              },
              "required": ["projectName"],
              "additionalProperties": false
            }
            """),

        new("firepit_focus_tab",
            "Bring an existing tab into focus and hand keyboard focus to its terminal. " +
            "No-op if the project isn't open.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string" }
              },
              "required": ["projectName"],
              "additionalProperties": false
            }
            """),

        new("firepit_close_tab",
            "Close the tab for a project, killing the running agent. No-op if not open.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string" }
              },
              "required": ["projectName"],
              "additionalProperties": false
            }
            """),

        new("firepit_reload",
            "Re-read <project>/.firepit/config.json and apply hot-reloadable parts (quick-links). " +
            "Pass restart=true to also restart the agent (needed for MCP / agent / env changes).",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string" },
                "restart":     { "type": "boolean", "description": "Also restart the agent.", "default": false }
              },
              "required": ["projectName"],
              "additionalProperties": false
            }
            """),

        new("firepit_inbox_list",
            "List pending (un-processed) inbox messages for a project. " +
            "Defaults to the calling agent's own project. Each entry contains " +
            "the id (filename) plus parsed frontmatter (from / subject / priority / date) " +
            "and the markdown body. Files moved to inbox/processed/ are excluded.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Project to inspect; omit for the caller's own project." }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_inbox_complete",
            "Mark an inbox message as processed by moving the file from " +
            "<project>/.firepit/inbox/<id> into the sibling inbox/processed/ folder. " +
            "Pass the 'id' returned by firepit_inbox_list. Idempotent.",
            """
            {
              "type": "object",
              "properties": {
                "id":          { "type": "string", "description": "Filename of the inbox message (as returned by firepit_inbox_list)." },
                "projectName": { "type": "string", "description": "Project containing the message; omit for the caller's own project." }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """),

        new("firepit_send_to",
            "Drop a markdown message into <toProject>/.firepit/inbox/. The receiving project's " +
            "Claude reads its inbox at session start (per the seed CLAUDE.md convention). " +
            "The 'from' field is taken from the calling agent's FIREPIT_PROJECT_NAME env var.",
            """
            {
              "type": "object",
              "properties": {
                "toProject": { "type": "string", "description": "Project name (folder name) to deliver to." },
                "subject":   { "type": "string" },
                "body":      { "type": "string", "description": "Markdown body." },
                "priority":  { "type": "string", "enum": ["low", "normal", "high"], "default": "normal" }
              },
              "required": ["toProject", "subject", "body"],
              "additionalProperties": false
            }
            """),

        new("firepit_add_command",
            "Add (or replace by name — this is an UPSERT, calling it with an existing 'name' " +
            "replaces that button in place) a toolbar/Run button in a project's .firepit/config.json " +
            "commands[]. Defaults to the caller's own project. Hot-reloads immediately — no restart. " +
            "Types: 'shell' spawns command+args; 'claude-prompt' pastes prompt into the live session; " +
            "'url' opens a browser. To delete a button, use firepit_remove_command. To list " +
            "current buttons, use firepit_list_commands. Note: this overwrites the JSONC file via " +
            "the structured serializer; the scaffold's tour-of-knobs comments are normalised away " +
            "on first use (the tool's input schema is the canonical reference from then on).",
            """
            {
              "type": "object",
              "properties": {
                "name":        { "type": "string", "description": "Button label; unique within the project (re-using a name updates that button)." },
                "type":        { "type": "string", "enum": ["shell", "claude-prompt", "url"] },
                "projectName": { "type": "string", "description": "Project to add to; omit for the caller's own project." },
                "icon":        { "type": "string" },
                "command":     { "type": "string", "description": "shell: executable, e.g. 'npm'." },
                "args":        { "type": "array", "items": { "type": "string" }, "description": "shell: e.g. ['run','dev']." },
                "prompt":      { "type": "string", "description": "claude-prompt: text injected into the PTY." },
                "url":         { "type": "string", "description": "url: target; {projectName}/{projectPath} substituted." },
                "cwd":         { "type": "string", "description": "shell: working dir relative to project root." },
                "env":         { "type": "object", "additionalProperties": { "type": "string" }, "description": "shell: extra env on the child." },
                "elevated":    { "type": "boolean", "description": "shell: Windows UAC (run as admin)." },
                "confirm":     { "type": "boolean", "description": "shell: modal 'Run?' before spawning." },
                "window":      { "type": "string", "description": "'new' (default) | 'reuse:<id>' (2nd click focuses existing window) | 'inline' (write into this tab's PTY)." },
                "longRunning": { "type": "boolean", "description": "shell: live running-indicator + right-click Stop kills the tree. Pair with reuse:<id> for watchers (npm run dev)." },
                "keepOpenOnError": { "type": "boolean", "description": "shell (windowed): close the console on success, keep it open on a non-zero exit so the error is readable. Replaces blanket -NoExit / '; pause'. Ignored for window:'inline'." },
                "group":       { "type": "string", "description": "Opt-in toolbar grouping. Commands sharing a group label collapse into one dropdown button (e.g. 'Run' for Build & Run / Debug / Release). Omit to render as a standalone button." }
              },
              "required": ["name", "type"],
              "additionalProperties": false
            }
            """),

        new("firepit_list_commands",
            "List a project's current toolbar commands (the entries in .firepit/config.json " +
            "commands[]). Defaults to the caller's own project. Read-only — covers 'find' too " +
            "via client-side filtering. Returns name/type/icon plus the type-specific fields.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Project to inspect; omit for the caller's own project." }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_remove_command",
            "Remove a toolbar command by name (case-insensitive). Idempotent — removing a name " +
            "that doesn't exist returns Ok with a 'not found' note. Defaults to the caller's " +
            "own project. Hot-reloads immediately.",
            """
            {
              "type": "object",
              "properties": {
                "name":        { "type": "string", "description": "Button label to remove." },
                "projectName": { "type": "string", "description": "Project to remove from; omit for the caller's own project." }
              },
              "required": ["name"],
              "additionalProperties": false
            }
            """),

        new("firepit_knowledge_search",
            "Hybrid (semantic + full-text) search over the knowledge bases — the committed " +
            "markdown files under <project>/.firepit/knowledge/. Scope 'project' searches the " +
            "calling project, 'global' the shared knowledge base (the .firepit meta project), " +
            "'both' (default) merges the two. Each hit carries its scope name and relative path; " +
            "pass those to firepit_knowledge_get for the full document. Prefer this over grepping " +
            "markdown files: it also matches paraphrases, not just exact keywords.",
            """
            {
              "type": "object",
              "properties": {
                "query":       { "type": "string", "description": "Natural-language question or keywords." },
                "scope":       { "type": "string", "enum": ["project", "global", "both"], "default": "both" },
                "projectName": { "type": "string", "description": "Project whose knowledge to search; omit for the caller's own project." },
                "limit":       { "type": "integer", "description": "Max documents returned.", "default": 8 }
              },
              "required": ["query"],
              "additionalProperties": false
            }
            """),

        new("firepit_knowledge_get",
            "Read one knowledge document in full. 'scope' is the scope name as returned by " +
            "firepit_knowledge_search hits ('global' or a project name; omit for the caller's " +
            "own project), 'path' the hit's relative path.",
            """
            {
              "type": "object",
              "properties": {
                "scope": { "type": "string", "description": "Scope name from a search hit; omit for the caller's own project." },
                "path":  { "type": "string", "description": "Document path relative to the scope's knowledge folder, e.g. 'conpty-resize-quirks.md'." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),

        new("firepit_blueprint_list",
            "List the available project blueprints (folders under the .firepit meta project's " +
            "blueprints/ directory). A blueprint is a declarative 'this must exist' manifest: " +
            "files to seed, .gitignore lines, CLAUDE.md sections. The built-in 'firepit' " +
            "blueprint is seeded on first use and can be edited on disk afterwards.",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """),

        new("firepit_integrity_check",
            "Verify that what Firepit maintains for a project still matches reality: every " +
            "knowledge document on disk is in the search index and up to date, the index is " +
            "readable, the CLAUDE.md fragment imports resolve and match the repository's CURRENT " +
            "visibility, and the project is blueprint-conformant. Omit projectName to sweep " +
            "everything. Findings marked \"error\" mean an agent is being actively misled — " +
            "knowledge that exists but cannot be found, or a public repo importing the private " +
            "policy. With repair=true, Firepit fixes what is derived and can be rebuilt from the " +
            "documents (reindex, rebuild a corrupt index, regenerate the pinned digest, re-attach " +
            "a lost file watcher); it never edits a document or a CLAUDE.md, so anything needing " +
            "that comes back as a finding with the fix spelled out.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Project to check; omit to check all projects." },
                "repair":      { "type": "boolean", "description": "Repair derived data (index, digest, watcher). Never touches documents or CLAUDE.md.", "default": false }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_blueprint_check",
            "Check project(s) for blueprint conformance without changing anything. Returns per " +
            "project the pending actions an apply would take (empty = conformant) plus warnings " +
            "(e.g. blanket .firepit//.claude/ gitignore lines that hide shared config). Omit " +
            "projectName to sweep every known project — the maintenance view for the .firepit " +
            "helper agent.",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Project to check; omit to check all projects." },
                "blueprint":   { "type": "string", "description": "Blueprint name.", "default": "firepit" }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_blueprint_apply",
            "Apply a blueprint to one project — the single idempotent operation: whatever is " +
            "missing gets created, whatever exists is never touched. 'New project' and " +
            "'modernise an old project' are this same call. Blanket gitignore fixes rewrite " +
            "user content, so they only happen with fixBlanketIgnores=true.",
            """
            {
              "type": "object",
              "properties": {
                "projectName":       { "type": "string", "description": "Project to apply to; omit for the caller's own project." },
                "blueprint":         { "type": "string", "description": "Blueprint name.", "default": "firepit" },
                "fixBlanketIgnores": { "type": "boolean", "description": "Also comment out blanket .firepit//.claude/ ignore lines.", "default": false }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_knowledge_add",
            "Save a new knowledge document as markdown under <scope>/.firepit/knowledge/ and index " +
            "it immediately. Write knowledge in English (indexing convention). Use 'global' for " +
            "cross-project knowledge (C# patterns, tooling lore); omit scope for the caller's own " +
            "project. The file is plain markdown in the repo — commit it like any other file. " +
            "To correct existing knowledge use firepit_knowledge_update instead of adding a " +
            "near-duplicate — stale duplicates poison search.",
            """
            {
              "type": "object",
              "properties": {
                "title":   { "type": "string", "description": "Document title; becomes the H1 and the slugged file name." },
                "content": { "type": "string", "description": "Markdown body (English). A leading '# ' heading is kept as-is." },
                "scope":   { "type": "string", "description": "'global' or a project name; omit for the caller's own project." },
                "pinned":  { "type": "boolean", "description": "Mark pin: true — the doc is auto-injected into every session at start via .firepit/knowledge-pinned.md. Reserve for always-on reflex rules; keep the pinned set small.", "default": false }
              },
              "required": ["title", "content"],
              "additionalProperties": false
            }
            """),

        new("firepit_knowledge_update",
            "Replace an existing knowledge document's content in place and re-index/re-embed it " +
            "immediately — the old phrasing stops matching, the new content becomes searchable. " +
            "This is the memory-hygiene tool: correct or refresh a stale doc here instead of " +
            "adding a duplicate. 'scope' + 'path' as carried by search hits. Hand-editing the " +
            "markdown file directly is also fine — a file watcher re-indexes external changes " +
            "automatically.",
            """
            {
              "type": "object",
              "properties": {
                "path":    { "type": "string", "description": "Document path relative to the scope's knowledge folder (from a search hit)." },
                "content": { "type": "string", "description": "New markdown body — replaces the entire document." },
                "title":   { "type": "string", "description": "Optional new title; prepended as H1 when the content brings no heading." },
                "scope":   { "type": "string", "description": "'global' or a project name; omit for the caller's own project." },
                "pinned":  { "type": "boolean", "description": "true = pin (auto-inject at session start), false = unpin. Omit to keep the doc's current pin state." }
              },
              "required": ["path", "content"],
              "additionalProperties": false
            }
            """),

        new("firepit_knowledge_delete",
            "Delete a knowledge document and remove it from the search index (chunks, full-text " +
            "rows and vectors — and from the pinned digest if it was pinned). The file is under " +
            "git, so the deletion shows up as a normal repo change. Deleting a missing doc " +
            "returns ok=false with a note.",
            """
            {
              "type": "object",
              "properties": {
                "path":  { "type": "string", "description": "Document path relative to the scope's knowledge folder (from a search hit)." },
                "scope": { "type": "string", "description": "'global' or a project name; omit for the caller's own project." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),

        new("firepit_artifact_add",
            "Pin a file into the project's artifact pane (the paperclip panel) so the user can " +
            "open it without hunting through folders. USE THIS whenever you produce something " +
            "the user will want to look at: a report, screenshot, diagram, generated image, " +
            "log excerpt, build output, or an executable you just built for them to run. " +
            "Links only — the file stays where it is and removing the entry never deletes it. " +
            "Upsert by file: re-adding the same path updates its label and note in place. " +
            "Executables are marked with a run icon in the pane, so linking one is fine and " +
            "honest. To drop an entry use firepit_artifact_remove.",
            """
            {
              "type": "object",
              "properties": {
                "path":        { "type": "string", "description": "File to link. Absolute, or relative to the project root (relative keeps working on another machine)." },
                "label":       { "type": "string", "description": "Display name in the pane; defaults to the file name." },
                "note":        { "type": "string", "description": "One line of context, e.g. 'crash repro, step 4'." },
                "projectName": { "type": "string", "description": "Project to add to; omit for the caller's own project." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),

        new("firepit_artifact_remove",
            "Remove a link from the project's artifact pane. The file on disk is never touched — " +
            "this only unpins it. Address it by 'path' or by the 'label' shown in the pane. " +
            "Idempotent: removing something that isn't pinned returns ok with a note. Use this " +
            "to clear artifacts that have gone stale rather than letting the pane silt up.",
            """
            {
              "type": "object",
              "properties": {
                "path":        { "type": "string", "description": "Linked path (absolute or project-relative)." },
                "label":       { "type": "string", "description": "Display label, if you don't have the path." },
                "projectName": { "type": "string", "description": "Project to remove from; omit for the caller's own project." }
              },
              "additionalProperties": false
            }
            """),

        new("firepit_artifact_list",
            "List what is currently pinned in the project's artifact pane: path, label, note, " +
            "kind (image/markdown/text/document/executable/archive/other) and whether the target " +
            "still exists. Read-only. Check this before adding, so you update an existing entry " +
            "instead of piling up near-duplicates, and to spot dead links (exists=false).",
            """
            {
              "type": "object",
              "properties": {
                "projectName": { "type": "string", "description": "Project to inspect; omit for the caller's own project." }
              },
              "additionalProperties": false
            }
            """),
    ];
}

internal sealed record McpToolDefinitionRaw(string Name, string Description, string InputSchemaJson);

internal static class McpResourceDefinitions
{
    public static readonly IReadOnlyList<McpResourceDefinition> All =
    [
        new("firepit://projects",
            "Projects",
            "Current project list with open/closed status and live session state.",
            "application/json"),
        new("firepit://sessions",
            "Open sessions",
            "Tabs currently open in Firepit, with their activity state (Burning/Embers/Dead).",
            "application/json"),
        new("firepit://settings",
            "Effective settings",
            "Global Firepit settings.json with secrets redacted (cred-references opaque, " +
            "key/token/secret/password keys masked).",
            "application/json"),
        new("firepit://config-schema",
            "Project config schema",
            "Canonical scaffold/schema for .firepit/config.json (JSONC with the full tour of knobs: " +
            "quickLinks, mcpActivations, agent, session, commands, scheduledJobs, runs). Read this " +
            "instead of guessing the shape when hand-editing a project's config.json.",
            "application/jsonc"),
    ];
}
