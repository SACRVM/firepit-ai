# Architecture

The technical contract: the pieces, how they talk, where the data lives, and
the lines that must not be crossed. What Firepit is for is in
[`SPEC.md`](../SPEC.md); how it got here is in [`LINUX.md`](LINUX.md).

## 1. Processes

```
 Claude Code session (desktop app or CLI)        one per project, many at once
 ├─ firepit plugin ─────────── reads .firepit/ files directly
 │                             writes through $.mcp.call("firepit", …)
 └─ firepit-mcp (stdio) ────── started by Claude Code from the project's .mcp.json
        │ length-prefixed JSON frames
        ▼
 $XDG_RUNTIME_DIR/firepit/mcp.sock   (0600, in a 0700 directory)
        │
        ▼
 firepitd ─── one per user (or per named instance), a systemd user service
   McpHost ──► FirepitWorkspace ──► projects · knowledge · inbox
                                     commands · blueprints · integrity · jobs
                                          │
                                          ▼
                      <projects root>/<project>/.firepit/   per project
                      <projects root>/.firepit/             central project
                      ~/.config/firepit, ~/.local/share/firepit
```

- **firepitd** (`src/Firepit.Daemon`) runs the workspace and the MCP host
  headless. It is long-lived and owns every write Firepit makes.
- **firepit-mcp** (`tools/firepit-mcp`) is a stateless tunnel that lives as
  long as its Claude Code session. Claude Code starts one per session that
  has the `firepit` server in its MCP configuration.
- **The mod** (`mods/firepit`) runs inside each Claude Code session,
  in the engine's plugin sandbox. It has no state beyond the session's. It
  reads project files, runs commands through `systemd-run`, and writes only
  through Firepit's tools.

## 2. Solution layout

| Project | Depends on | Holds |
|---|---|---|
| `Firepit.Core` | — | Settings and paths, project discovery, project config and its mutators, inbox reading, blueprints and fragments, the job scheduler, secrets, the server instructions |
| `Firepit.Knowledge` | Core | Knowledge store and layout, indexer and chunker, hybrid search, embeddings, the pinned digest, integrity |
| `Firepit.Process` | Core | Process runners: jobs (`claude -p`), the pseudo-terminal channel, the Secret Service lookup |
| `Firepit.Adapters` | Core | The Claude Code adapter: command lines for jobs and sessions |
| `Firepit.Mcp` | Core | The MCP host: protocol, tool and resource catalogue, socket and pipe servers, `IMcpBackend` |
| `Firepit.Workspace` | all of the above | `FirepitWorkspace`, the `IMcpBackend` with nothing above it but `ITabHost` |
| `Firepit.Daemon` | Workspace | `firepitd`: lock, signals, logging, rescan loop |
| `tools/firepit-mcp` | — | The bridge, with no project references on purpose |

**Invariants.**

1. **Libraries know no host.** Core, Knowledge, Process, Adapters and Mcp
   target plain `net10.0` and stay portable. Platform differences go
   through `PlatformText` (path comparison) and `FirepitPaths`.
2. **The workspace has no UI.** Anything a shell would do (tabs, toasts,
   applying config to a session) goes through `ITabHost`. The daemon passes
   `HeadlessTabHost`, which answers "no shell" and logs notifications.
3. **The bridge is a tunnel.** It translates frames and nothing else; the
   protocol lives in `McpHost`. It references no Firepit assembly, so it
   duplicates the socket-path rule (see §3).
4. **One writer per file.** `.firepit/config.json` and inbox moves are
   written by the workspace. The mod writes nothing in a
   project; it calls the tools.
5. **Neutral code vocabulary.** `Project`, `InboxMessage`, `JobScheduler`;
   the fire stays in user-facing copy.

## 3. The bridge and its protocol

Claude Code speaks newline-delimited JSON-RPC on the bridge's stdin and
stdout. Towards the daemon each message is a frame: 4 bytes of
little-endian length, then the UTF-8 payload, with at most 16 MiB per frame.

1. **Connect.** The socket is `$XDG_RUNTIME_DIR/firepit[-<instance>]/mcp.sock`;
   without `XDG_RUNTIME_DIR` it is `/tmp/firepit-<user>/…`. When nothing
   listens there, the bridge runs `systemctl --user start --no-block
   firepit.service` (or `firepit@<instance>.service`) once and keeps
   retrying for up to 15 s.
2. **Context frame.** The bridge's first frame is
   `{"_firepit_context":{"projectName":…,"pid":…,"cwd":…}}`. `projectName`
   comes from `FIREPIT_PROJECT_NAME` and is empty under Claude Code
   desktop. The host then takes the project whose path is the longest
   prefix of `cwd`. That project is the default `projectName` of every tool
   and the `from` of `firepit_send_to`.
3. **Tunnel.** After the context frame the bridge relays frames both ways
   until either side closes.
4. **Errors.** A bridge that cannot connect writes a JSON-RPC error (code
   -32099, id null) to stdout and the same line to stderr, so the message
   shows up in Claude Code's MCP view.

`FIREPIT_INSTANCE` selects a named instance; the daemon takes `--instance`.
On Windows the same bridge dials the named pipe `firepit-mcp[-<instance>]`
of the WPF shell on the `windows-legacy` branch.

## 4. The MCP host

`McpHost` implements `initialize`, `tools/list`, `tools/call`,
`resources/list`, `resources/read` and `ping`. It accepts up to 64 clients
at once.

- **Instructions.** `initialize` returns `FirepitServerInstructions.Text`, a
  short brief on the habits the tools exist for: show files instead of leaving paths in scrollback,
  close inbox messages you handled, search before researching. Every
  session reads it, so it has a size budget enforced by a test, and it must
  be English.
- **Tools.**
  - Projects: `firepit_list_projects`, `_open_project`, `_create_project`,
    `_rename_project`, `_reload`.
  - Commands: `firepit_add_command`, `_list_commands`, `_remove_command`.
  - Inbox: `firepit_send_to`, `firepit_inbox_list`, `_complete`.
  - Knowledge: `firepit_knowledge_search`, `_get`, `_add`, `_update`,
    `_delete`.
  - Blueprints: `firepit_blueprint_list`, `_check`, `_apply`.
  - `firepit_integrity_check`.
  - The tab tools (`firepit_open_tab`, `_focus_tab`, `_close_tab`) and the
    `firepit://sessions` resource only exist with a shell (`hasShell`); the
    daemon leaves them out.
- **Resources.** `firepit://projects`, `firepit://settings` (secrets
  redacted) and `firepit://config-schema` (the commented scaffold of §6).
- **Serialisation.** Calls arrive on connection threads. The workspace runs
  everything that touches shared state through one gate (`SemaphoreSlim`
  plus an `AsyncLocal` flag for re-entry), which replaces the UI thread the
  WPF window used to serialise on. Knowledge searches run outside the gate.

## 5. The daemon

`firepitd [--instance <name>] [--no-jobs]`

- **One per instance.** An exclusive lock on `<runtime>/firepitd.lock` makes
  a second start exit at once.
- **Lifecycle.** It starts the workspace (config migration, the central
  project seeded if the projects root has none, project scan, knowledge
  service, job scheduler), then the MCP host. SIGTERM and SIGINT stop it
  cleanly and remove the socket.
- **Rescan.** Every 30 s it compares the settings file's modification time
  and a fingerprint of the projects root. A fresh clone shows up without a
  restart, and a quiet root costs only a directory listing.
- **Logs.** Serilog writes to `~/.local/share/firepit/logs/firepitd-*.log`
  (14 files, 10 MB each) and to the console, which systemd puts in the
  journal. Logs name things (projects, tools, message subjects) and
  outcomes; they never contain message bodies or document text.
- **Start menu.** The default instance keeps a *Projects* submenu in the
  freedesktop menu: one entry per project in
  `~/.local/share/applications/firepit/`, a merged menu in
  `~/.config/menus/applications-merged/` and its `.directory` file. It syncs
  them at start and after every rescan that changed the project list, then
  asks KDE to rebuild its menu cache (`kbuildsycoca6`). A named instance
  writes no entries.
- **Opening a project.** `firepitd open <folder>`, which is what the entries
  run, and `firepit_open_project` open the project in the Claude desktop
  app. They pick the most recently active session the app keeps in that
  folder (read from the app's own records under
  `~/.config/Claude/claude-code-sessions/`, skipped when unreadable; worktree
  and archived sessions do not count) and open it with
  `claude://claude.ai/epitaxy/<id>`. With no such session they start one with
  `claude://code/new?folder=<folder>`. Inside the service the link goes
  through `systemd-run`, so a Claude app started that way does not belong to
  the service and survive its restarts.
- **Units.** `packaging/systemd/firepit.service` and `firepit@.service` run
  `~/.local/opt/firepit/firepitd` with `Restart=on-failure`. The PATH includes
  `~/.local/bin` and `~/.dotnet/tools`. `scripts/install-linux.sh` publishes
  the daemon, packs and installs the bridge as a .NET global tool, installs
  the units and restarts the service.

## 6. Data

| What | Where (Linux) |
|---|---|
| Settings | `~/.config/firepit/settings.json`, optional |
| State, logs, embedding model | `~/.local/share/firepit/` (`state.json`, `logs/`, `models/`) |
| Socket, lock | `$XDG_RUNTIME_DIR/firepit/` |
| A named instance | the same with `firepit-<name>` |
| Per project | `<project>/.firepit/` |
| Central project | `<projects root>/.firepit/` |

Windows keeps `%APPDATA%\Firepit` and `%LOCALAPPDATA%\Firepit`. Paths
compare case-sensitively on Linux and case-insensitively on Windows and
macOS (`PlatformText`).

**Projects.**
- Every folder directly under the projects root that has a `CLAUDE.md` or a
  `.claude/` folder is a project; manual entries in the settings add others.
- A project's name is its `config.json` `id`, else its folder name.
- The central project `.firepit` is a project too. It also holds what
  belongs to the whole tree: the global knowledge base (`knowledge/`), notes
  about other projects (`projects/<name>/`), shared `CLAUDE.md` fragments
  (`projects/claude*.md`) and the blueprints (`blueprints/`).

**`.firepit/config.json`** (JSONC, version 1):

| Key | Meaning |
|---|---|
| `id` | The project's name |
| `commands[]` | `name`, `group`, `description`, `bar`, `type` (`shell` · `claude-prompt` · `url`), `command`, `args`, `cwd`, `env`, `window` (`new` · `inline` · `reuse:<id>`), `longRunning`, `keepOpenOnError`, `confirm`, `prompt`, `url`, `disabled` |
| `scheduledJobs[]` | `name`, `prompt`, `schedule` (cron), `enabled`, `timeoutSeconds`, `timezone`, `onConcurrent`, `allowedTools`, `maxTurns`, `maxBudgetUsd`, `skipPermissions` |
| `runs` | `retentionDays` |
| `quickLinks`, `mcpActivations`, `agent`, `session` | read for old configs; the mod shows `quickLinks` as a *Links* group, the rest only mattered to the shell |

Commands are unique by group and name. The tools upsert and remove that way
and keep every field, `description` and `bar` included. A rewrite through
the tools drops the file's comments; the scaffold says so.

**Inbox.**
- Messages are files `.firepit/inbox/<UTC time>-from-<sender>-<subject
  slug>.md`, with frontmatter `from`, `to`, `subject`, `sentAt` and
  `priority`, then a markdown body.
- `firepit_inbox_complete` moves a file to `inbox/processed/`. Ids are bare
  file names; anything with a path separator is refused.

**Files to show.** Firepit keeps no list of them. A session shows a file
the way its client offers: the Claude desktop app sends it into the
conversation or publishes a page as a Claude artifact. The server
instructions teach that habit. The artifact list of earlier versions
(`.firepit/artifacts.json`, three tools, a pane) was retired in 0.7.0 of the
plugin; blueprint manifest v4 drops its `CLAUDE.md` section.

**Knowledge.**
- A scope is a project or the global base. Its documents are markdown files
  with optional frontmatter (`title`, `pin`). They live in
  `.firepit/knowledge/`, or, when that path is a pointer file, in the
  central project's `projects/<name>/knowledge/`. That keeps notes about a
  public repository out of it.
- Each scope has a derived index, `knowledge.db`: SQLite with FTS5 and
  sqlite-vec over chunks embedded with all-MiniLM-L6-v2 (ONNX Runtime,
  downloaded once to `models/`).
- Search ranks full-text and vector hits together. It falls back to
  full-text alone, and says so, when the model or `vec0` is not available.
  `vec0.so` is found under the portable RID even when a distribution's
  runtime reports its own RID.
- Pinned documents are collected into `.firepit/knowledge-pinned.md`, which
  `CLAUDE.md` imports.

**Jobs.**
- The scheduler enumerates `scheduledJobs` across all projects on each tick
  and evaluates cron in the job's timezone.
- After a downtime it catches up one missed occurrence at most. Each job
  runs at most once at a time; `onConcurrent` decides what happens to an
  occurrence while it runs.
- A run is `claude -p <prompt> --output-format json` with the job's limits,
  in the project directory. The `claude` CLI must be on the service's PATH.
  Records go to `.firepit/runs/<job>/<UTC>.json` and are kept for
  `retentionDays`.

**Blueprints and fragments.**
- A blueprint (`<central>/blueprints/<name>/`) lists the `CLAUDE.md`
  sections a project carries, the files it seeds and the ignore rules it
  needs. Sections are matched by a marker string, so text the user wrote
  around them is never touched.
- The shared fragments are imported, not copied. Which one a project gets
  depends on whether its GitHub repository is public or private.
  `firepit_integrity_check` notices when that changes.

## 7. The mod

`mods/firepit` is a Claude Code plugin with one hooks module
(`hooks/register.tsx`), pure logic beside it (`logic.ts`, `inbox.ts`)
and a state contract (`types/index.d.ts`). The repository is
its own plugin marketplace, so the installed mod is read from the working
copy.

- **Session start.** The mod registers `/fire` and reads the config, the
  units and the inbox. Every 2 s it re-reads what changed (the config by
  modification time, the inbox by a listing signature). Every 3 s it lists
  the project's systemd units.
- **State.** Session state is written only when a value changed. Every write
  redraws the bar and the panel, and a click that lands on a drawing just
  replaced is lost.
- **Commands.**
  - `longRunning` runs `systemd-run --user --unit
    firepit-<project>-<id>`, with logs from `journalctl`.
  - A windowed command opens Konsole in a transient unit.
  - `inline` runs through `$.process.run`, and the output comes back as
    the `/fire` command's result: a row in the chat, and context for the
    model.
  - Shell commands need trust: a SHA-256 over their command lines, stored
    in the mod's store per project root.
- **Inbox delivery.**
  - When a message arrives and the session is idle, the mod submits a
    prompt under its own name: `tell` asks for a summary and a wait, `act`
    for the work. Idle means no turn running (`turn.start` /
    `turn.complete`, cross-checked with the prompt band's `isWorking`), an
    empty prompt box, and two sweeps in a row.
  - What was pending when the session started is never delivered on its
    own. `off` marks arrivals as seen.
  - The mode is stored per project root.
- **Viewer.** An inbox message opens in a second pane, up to 60 000
  characters of markdown.
- **Skills.** `mods/firepit/skills/<name>/SKILL.md`: `setup`, `commands`,
  `messages`, `knowledge`, loaded by an agent when its description matches
  the work. The server instructions name them, so a session knows they
  exist. `PluginSkillsTests` checks that each names only existing tools, that
  the instructions point only at existing skills, and that all of it is
  English.

## 8. Security boundary

- **Same user, same machine.** The socket is the only way in, mode 0600 in
  a 0700 directory. There is no TCP listener and no authentication beyond
  the file mode.
- **Paths are checked.** Projects are addressed by name. Inbox ids must be
  bare file names; knowledge documents are addressed by scope and a path
  inside it; `create_project` and `rename_project` work under the projects
  root.
- **Secrets stay references.** `${env:…}` and `${cred:…}` in settings;
  `firepit://settings` masks keys, tokens, secrets and passwords.
- **Commands wait for trust**, per exact command line. A change asks again.
- **Delivered messages are fenced.** They arrive under the mod's name, ask
  before anything irreversible, and are not forwarded to other projects.
- **Jobs run with limits.** Tools, turns and budget come from the project's
  config; `skipPermissions` is off unless the config says otherwise.

## 9. Testing

`dotnet test Firepit.slnx` runs xUnit across Core, Knowledge, Process,
Adapters and Workspace. A handful of facts describe Windows behaviour and
run only there (`WindowsFact`).

- Process tests use real `/bin/sh` and a fake `claude` script.
- Workspace tests run the headless workspace against a temporary projects
  root, including concurrent creates through the gate.
- Knowledge tests cover the index, both search paths and pointer files.

The mod has `claude plugin test mods/firepit`: pure-logic tests,
plus UI and delivery tests that mount the bar and the panel on the desktop
and terminal surfaces against a mocked engine and clock. CI is one job on
ubuntu-latest.

## 10. Versions and releases

- `firepitd` and `firepit-mcp` share a version (`<Version>` in both csproj
  files). The mod versions itself in `plugin.json`.
- `/release` bumps both, closes the changelog section, tags `v*`, publishes
  the GitHub release and reinstalls the service here.
- Milestone tags are `m*`.
