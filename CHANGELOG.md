# Changelog

All notable changes to Firepit. Format roughly follows [Keep a Changelog].
Versioning follows SemVer; pre-1.0 minor bumps may include breaking changes.

## [Unreleased]

## [0.19.0] — 2026-08-17

### Added

- **Shared CLAUDE.md fragments, imported instead of copied.** Three files in
  the central repo under `projects/` — `claude.md` for every managed
  project, plus `claude-github-public.md` and `claude-github-private.md` for
  the two repo classes. The blueprint writes the `@` imports into a
  project's CLAUDE.md; the text itself stays in one place, so changing a
  convention is one edit rather than thirty-three that fall out of step.

  They sit under `projects/` rather than at the repo root because
  `<meta>/CLAUDE.md` already exists and addresses an agent working *inside*
  the central repo. One file cannot be both "instructions for this repo" and
  "instructions for all repos" — the same conflation the knowledge layout
  had one level down.

  Public and private genuinely differ: in a public repo research belongs in
  the knowledge base rather than in committed files, and the history is
  permanent. Which fragment a project imports is decided once, at apply
  time, by asking `gh` for the repository's visibility. Anything unknown —
  no git, no GitHub remote, no `gh`, a call that fails — counts as public.
  Treating a private repo as public costs one unnecessary rule; the reverse
  invites research into a repository anyone can read.

  The import path is relative where that resolves and absolute where it
  cannot, so a project on a network share works as well as one next to the
  central repo.

  The fragments are seeded once and never overwritten. They exist to be
  edited, and an edit that gets reset on the next launch would be worse than
  no fragment at all.

  They carry policy, not tool documentation. Behaviour arrives with the MCP
  handshake, which ships in the executable and changes with a release; a
  fragment changes with a text editor. Keeping the two apart is what stops
  them becoming two copies of the same conventions — a test asserts the
  fragments do not name the tools.

## [0.18.0] — 2026-08-17

### Added

- **A project settings dialog.** Left-click Configure in the tab toolbar; the
  right-click menu still opens `config.json` directly for everything the
  dialog does not cover. It owns one setting so far — where this project's
  knowledge lives — because that one otherwise means hand-writing a relative
  path into `.firepit/knowledge` and getting the `../..` right.

  Three choices: in this repo, hosted privately in the `.firepit` repo, or
  any other folder. Existing documents move with the setting, the dialog
  says how many before you commit to it, and it refuses to merge two
  populated knowledge bases rather than silently overwriting same-named
  files. Pointing several projects at one folder is how they end up sharing
  a base.

### Changed

- **The global knowledge base moved out of the meta project's own folder.**
  `<meta>/.firepit/knowledge/` was doing two jobs: the base every project
  reads, and the administration project's own local notes. Anything saved
  globally was also `.firepit`'s project knowledge, and the other way round.

  The rule that broke is worth stating plainly, because everything else
  follows from it: `<repo>/.firepit/` holds what belongs to *that repo*.
  What the meta project holds about *other* projects cannot live there. So
  the global base is now `<meta>/knowledge/` — the knowledge of the whole
  tree rather than of one repo in it — and hosted stores for other projects
  are `<meta>/projects/<name>/knowledge/`. The meta project keeps
  `.firepit/knowledge/` for its own notes, exactly like every other project.

  Nothing moves on its own. While `<meta>/knowledge/` does not exist,
  Firepit keeps reading the old directory as the global base and does not
  give the meta project a separate local scope — starting an empty global
  base would quietly demote every existing doc to one project's notes.

## [0.17.0] — 2026-08-17

### Changed

- **`.firepit/knowledge` is either a directory or a pointer file.** A
  directory means the docs are in it, as always. A file means its content is
  the path to the directory that really holds them — relative to the
  `.firepit` directory, so moving the whole repos tree keeps every pointer
  valid. This is the pattern git uses for `.git`, which is a directory in a
  normal clone and a file containing `gitdir:` in a worktree.

  It replaces the `knowledge.storage` config key from 0.16.x, which is gone
  and was never written into a config. The key read as "store my knowledge
  in project X", but what it did was create a separate folder that merely
  *lived* there — and the distance between those two readings is "my
  research got merged into the base every project reads". A path in a file
  has no value space, so there are no reserved words, no collisions with
  project names, and nowhere to write something unsupported.

  A pointer must land on a directory. Pointing at another pointer is
  refused rather than followed, which is what keeps resolution at exactly
  one step — no hop limit, no cycles to detect.

  Several projects may point at one directory; that is one knowledge base,
  not one per project, and it is named after the directory. Five public
  repos can share one private base without any of them hosting it.

  Errors are loud on purpose: an empty pointer, a path whose parent does not
  exist, or a pointer at a pointer disables the scope and says why. Falling
  back to the project's own folder would put research into the repo the
  pointer exists to keep it out of.

## [0.16.1] — 2026-08-17

### Changed

- **`knowledge.storage` defaults to `"repo"`, not `"self"`.** The name that
  was agreed. `"self"` shipped in 0.16.0 for about twenty minutes and is
  gone rather than aliased — two words for one setting is the confusion the
  value space was reorganised to avoid.

  Naming the project itself does the same thing as `"repo"`, which is the
  way out should a project ever genuinely be called "repo". That leaves
  exactly one reserved word in the value space.

## [0.16.0] — 2026-08-17

### Added

- **A project can keep its knowledge in another project's repo.** Knowledge
  docs were always committed next to the project, which leaves a public
  repo one bad choice: publish the research, or don't keep it. The new
  `knowledge.storage` setting in `.firepit/config.json` names the project
  whose store holds the docs — they land in
  `<that project>/knowledge/<this project>/` instead. Default is `"self"`,
  so nothing changes for a project that says nothing.

  The value is a project name, not an enum, which is why it also covers the
  case it was not designed for: a dedicated private knowledge repo already
  works as a target, with no second mechanism. `".firepit"` — the meta
  project — is the expected answer for most projects.

  A name that resolves to no project **disables the scope** and says why,
  rather than falling back to `"self"`. The fallback is the dangerous
  outcome here: it would write private research into the public repo the
  setting exists to keep it out of.

  What survives the redirect: `knowledge-pinned.md` stays in the project,
  because CLAUDE.md imports it by a path relative to the project root. It is
  excluded via `.git/info/exclude` rather than `.gitignore` — a gitignore
  entry is itself committed and would advertise in a public repo that a
  private file exists and what it is called.

### Fixed

- **The knowledge tools no longer tell you to commit a file that isn't in
  your repo.** `firepit_knowledge_add` / `_update` / `_delete` ended every
  reply with "Remember to commit the file". For a project whose docs live
  in another project's store that sends the agent hunting for a change this
  repo does not have; the reply now names the project that actually holds
  the file.

## [0.15.0] — 2026-08-17

### Added

- **Firepit tells agents about itself at the handshake.** The MCP protocol
  carries an `instructions` field in the `initialize` result, and ours was
  empty. It now holds Firepit's conventions — pin artifacts as you produce
  them, close what you read in the inbox, search the knowledge base before
  researching, how to address another project — so every session in every
  project starts knowing them.

  This replaces copying the same prose into each project's `CLAUDE.md` as
  the primary channel, and fixes two things that were wrong with copying.
  Copies go stale: the blueprint carries a manifest version and a one-time
  top-up migration purely to chase them, while instructions ship inside the
  executable and are current by construction. And copies are committed:
  a public repo carried Firepit's conventions in its own `CLAUDE.md`, where
  they are noise to everyone who does not run Firepit. The blueprint
  sections stay as a fallback for agents that ignore the field.

  Tool descriptions could not do this job. A description is only read once
  the agent is already reaching for that tool, which never happens for a
  habit nothing prompted — the reason the artifact pane went unused while
  its tools were documented all along.

## [0.14.1] — 2026-08-15

### Fixed

- **A project that declares an `id` can address itself again.** The
  registry keys projects on the folder name, but a session exports
  `FIREPIT_PROJECT_NAME` from the `id` in `.firepit/config.json` when one
  is set. Those are allowed to differ, and when they do the agent knows
  itself by a name the registry has never heard of — turning every tool
  with a `projectName` parameter into a trap for that project, its own
  default path included. The meta project is the live case: folder
  `.firepit`, id `firepit-central`. Lookup now falls back to the
  configured id, on a miss only.
- **An empty list means an empty list.** `firepit_inbox_list`,
  `firepit_list_commands` and `firepit_artifact_list` answered an unknown
  project with no entries, which reads exactly like "nothing here" — so
  an agent that correctly checked its inbox was told it was clear and
  stopped looking, while messages sat waiting. A name that resolves to
  nothing is now an error naming both ways to address a project.

## [0.14.0] — 2026-08-15

### Added

- **Inbox messages deliver themselves.** A message arriving for a project
  with an open, idle tab is handed to that project's agent on its own —
  no Inbox button, no typed "work your inbox". Which prompt it carries
  depends on where the message lands: a background worker is told to act
  and report back, while the tab the user is actually looking at is told
  to summarise and wait for a go. That makes her active tab the single
  human gate, and the circuit breaker with it — any chain routing back to
  her stops there without a separate loop guard.

  Delivery only ever targets a session sitting idle across consecutive
  sweeps, so nothing arrives mid-turn. Idle is a trustworthy signal here
  because the activity detector already pins Burning while the agent
  reports progress over OSC 9;4 — thinking and tool calls produce no
  output but do not read as finished. No agent output is parsed for any
  of this; the host stays transparent, and the safety gate stays in the
  prompt where the agent can actually judge it.

  The inbox remains the durable transport: if no tab is open, the session
  is busy, or it died, the message simply stays in the folder with its
  badge. Turn the whole thing off with
  `platform.inboxAutoDeliverEnabled: false` in `settings.json`.

## [0.13.1] — 2026-08-15

### Added

- **Discard a message from its own row.** The inbox list only offered
  deletion through a bottom-bar button acting on the selected row, so the
  hand went looking for an ✕ at the end of the row and found nothing.
  There is one now, revealed on hover or selection, with the same
  confirm. Rows also select in extended mode, so several can be cleared
  before handing the rest over; the bottom Delete now appears only when a
  selection spans more than one row.

### Changed

- **The inbox prompt reads the queue over MCP.** It used to point the
  agent at `.firepit/inbox/*.md` on disk while telling it to finish with
  `firepit_inbox_complete` — two mechanisms for one queue. The agent now
  reads with `firepit_inbox_list` and completes on the same surface, and
  the prompt no longer depends on the folder layout. Bodies still never
  travel through the prompt itself.

### Fixed

- **Handing a prompt to the agent no longer needs a manual Enter.** The
  carriage return rode along in the same PTY write as the text, and agent
  TUIs treat a burst arriving in one read as a paste — so the return
  counted as a newline inside the pasted text rather than as submit, and
  the prompt sat in the input box waiting for a keypress, right after the
  button that was supposed to send it. It now goes as its own keystroke
  once the paste window has closed.

## [0.13.0] — 2026-08-15

### Added

- **Agents now know the artifact pane exists.** The tools shipped with good
  descriptions and near-zero adoption, because a tool description is only
  read once the agent is already looking for that tool — nothing ever
  prompted the thought "I just produced a file the user will want to
  open". Artifacts get the same treatment as the inbox and knowledge
  conventions: a CLAUDE.md section, seeded into new projects by the
  scaffold and applied to existing ones via `firepit_blueprint_apply`.
- **Blueprint manifests carry a version and migrate once.** Previously the
  default blueprint was written on first use and then frozen, so a
  convention added in a later release could only ever reach fresh
  installs. Built-in sections introduced by a newer Firepit are now
  appended by marker, leaving every user field and reworded section
  untouched. Past the migration the file is yours again — a section you
  delete stays deleted.

### Changed

- **Inbox triage is a list, not a wizard.** Every pending message on one
  screen as sender, subject, age and a priority dot; the body moves below
  the list, to look at rather than to read before acting. Five actions
  become one primary plus two for the selected row. The primary action
  hands the whole queue to the project's agent in a single prompt —
  replacing the "work your inbox" that otherwise gets typed by hand — and
  that prompt carries a standing rule: act, but stop and ask before
  anything irreversible. The rule lives in the prompt rather than in
  Firepit on purpose: judging whether an instruction is destructive means
  reading the message, which a transparent host does not do.
- **The shell-command trust dialog leads with the commands.** It used to
  open with three lines of prose about cloned repos before showing what
  would actually run, so the list got skipped along with the warning.
  Commands first, admin rows marked, one line of context, and the button
  says Allow.

### Fixed

- **The inbox body never wrapped.** Its scroll container allowed
  horizontal scrolling, which measures the child at infinite width and
  silently defeats `TextWrapping` — so long markdown ran off to the right
  behind a scrollbar. The redundant outer scroller is gone; the text box
  scrolls itself.
- **Horizontal scrollbars rendered on the wrong axis.** The app-wide
  scrollbar style only ever templated the vertical case: the track never
  inherited the bar's orientation and the page commands were hardcoded, so
  any horizontal bar came out laid sideways and scrolled the wrong way.
  The inbox was simply the first place that produced one.
- **Focusing the terminal could leave the keyboard nowhere.** Focus is two
  stages — WPF focuses the WebView2 host, then a bridge message focuses
  xterm's textarea — and stage two was skipped whenever the bridge wasn't
  up yet, leaving the keyboard on a control that drops every keystroke.
  It now waits for the ready handshake instead of relying on a user click.
- **Focus diagnostics.** Intermittent focus theft leaves no trace, so the
  two moments that identify it are now logged: window deactivation
  together with the process that took the foreground, and keyboard focus
  going to nothing.

## [0.12.0] — 2026-08-14

### Changed

- **The GitHub account behind Firepit is now `github.com/SACRVM`.** The
  in-app update check, the meta-project CLAUDE.md template, the
  installer's publisher/support/updates URLs and the About byline all
  point there directly — no reliance on GitHub's rename redirect.

### Fixed

- **MCP tools no longer vanish in the ninth tab.** The named-pipe host
  posted 8 accept loops, but a loop only created its replacement listener
  *after* the client it accepted disconnected — and an agent session holds
  its slot for the whole life of the tab. So the pool of listening
  instances shrank with every open session, and once eight were connected
  nothing was listening: the next tab's `firepit-mcp` bridge timed out and
  that session came up with zero `firepit_*` tools. Accepted clients are
  now served on their own task and the loop immediately re-posts a
  listener; the instance ceiling went 8 → 64, and exhaustion is logged as
  a warning instead of being invisible above the OS layer.
- **`firepit-mcp` stopped blaming the wrong thing on connect timeout.**
  `ConnectAsync` throws the same `TimeoutException` for "pipe doesn't
  exist" and "every instance is busy"; the bridge reported *"Firepit GUI
  is not running"* for both, which is what disguised the slot exhaustion
  above as a random defect. It now checks whether the pipe exists and says
  so: *"Firepit is running, but all of its MCP connection slots are in
  use."*
- **`firepit-mcp.exe`'s file-version resource** was pinned at `0.5.0`
  while the MCP handshake reported the real version. Bumped, and
  `/release` now keeps both csproj versions in sync.

### Removed

- **The one-shot v0.5.16 legacy quick-link strip** (issue #14). It matched
  the two pre-v0.5.0 seeded quick-links by their exact literal URLs; six
  minor versions on, every live install has long since run it. Upgrading
  from a pre-v0.5.16 build now simply keeps those two entries — remove
  them via Settings → Quick-links.

## [0.5.20] — 2026-05-18

### Fixed

- **MCP `firepit_*` tools are now always available** (issue #11 followup).
  The built-in Firepit MCP server (Inbox, `firepit_send_to`, project
  control) used to require an explicit `mcpActivations: [{ "id":
  "firepit" }]` entry in each project's `.firepit/config.json` — without
  it the spawned Claude session had 0 firepit tools and the toolbar
  Inbox button produced a prompt no agent could fulfil. The built-in
  is now implicitly projected for every project. Users who list it
  explicitly (e.g. to pass `envOverrides`) win — no duplicate spawn.
- **Drag-and-drop images from clipboard / Snipping Tool / browser** now
  work. `FileDropTarget` accepted only `CF_HDROP` (Explorer files);
  in-memory bitmaps (`CF_DIB`) were silently rejected. v0.5.20 adds
  CF_DIB support: the DIB is wrapped as BMP, decoded through WPF
  imaging, persisted as PNG to `%LOCALAPPDATA%\Firepit\dragdrop\` and
  the path is pasted into the terminal just like a real file drop.
  Claude Code sees a normal file path it can read.
- **Tab resume reliability.** Restored tabs that weren't the active
  tab were losing their `--continue` flag on every restart — a
  SelectionChanged race during the tab-restore loop start-and-cancelled
  deferred sessions once, consuming the sidecar `_deferredResume`
  dictionary entry. Clicking the tab later then opened a fresh session
  with no agent-history continuity.
  - `PendingResume` flag now lives on `SessionTab` itself, not in a
    MainWindow dictionary — survives any number of phantom cancel /
    restart cycles.
  - Cancelled `StartSessionAsync` resets `_initialized` and notifies
    Dead, so the tab can actually be retried instead of staying frozen
    in Igniting.
  - New `SessionTab.RestartIfPending()` is the idempotent wake entry
    point used by both tab-selection and project-list clicks.

## [0.5.19] — 2026-05-18

### Fixed

- **Inbox button polish.** Three small bugs in the v0.5.15 toolbar Inbox
  flow that bit during daily use:
  - Modal title, body, button labels and the prompt handed to Claude
    are now English, matching the rest of the app. They were German by
    accident (author's working language) and stuck out in an otherwise
    English-only product.
  - The prompt is now submitted with `\r` (CR) instead of `\n` (LF).
    Claude Code's TUI treats LF as a newline inside the input buffer
    and CR as submit — so the prompt now starts running immediately
    instead of sitting in the input waiting for the user to hit Enter.
  - Focus is handed back to the terminal after the prompt is sent.
    Previously focus stayed on the Inbox toolbar button, so the user's
    Enter (to submit) re-triggered the button and re-opened the modal.

## [0.5.18] — 2026-05-18

### Added

- **Toolbar quick-commands Phase B** (issue #11). Shell-type entries in
  `.firepit/config.json` `commands[]` gain three new lifecycle knobs:
  - `window: "new"` (default, unchanged) — spawn a fresh OS console window
    each click. Same as Phase A.
  - `window: "reuse:<id>"` — first click spawns the process and registers
    it under the id; subsequent clicks bring its console window to the
    foreground instead of spawning a duplicate. Per-project scope. The id
    is yours to pick (e.g. `"dev"`, `"relay"`); two commands sharing an
    id share the slot.
  - `window: "inline"` — write the command line into the active tab's
    PTY so the session's shell or agent executes it. `cwd` / `env` /
    `elevated` are ignored in this mode — the PTY owns its environment.
  - `longRunning: true` — toolbar button renders a burning-warm live dot
    while the child process is alive; right-click → "Stop" kills the
    process tree. Typically combined with `reuse:<id>` for dev-loop
    watchers (`npm run dev`, `python relay_proxy.py`, `dotnet watch`).
- **Scaffold doc.** New `commands[]` JSONC scaffold spells out all of
  the Phase A + Phase B knobs with copy-paste examples.

### Notes

- Tab close does **not** stop tracked long-running children — by design.
  The user opened these watchers deliberately; Firepit going away
  shouldn't take them down. Use the right-click Stop, or close the
  console window yourself.
- UAC-elevated children can't be killed by the non-elevated Firepit
  parent. The toolbar entry stays registered until the child exits on
  its own; Stop is a no-op (logged at debug).

## [0.5.17] — 2026-05-17

### Added

- **Toolbar quick-commands gain `cwd` / `env` / `elevated` / `confirm`**
  (issue #11 Phase A). `.firepit/config.json` `commands[]` entries with
  `type: "shell"` can now declare:
  - `cwd` — relative (joined onto project root) or absolute. Default =
    project root.
  - `env` — extra env vars merged onto the spawn (null = remove key,
    same semantics as `mcpOverrides`).
  - `elevated: true` — Windows: `Verb=runas` triggers UAC. Declined
    prompts are treated as a choice (no error). Required for things
    like `bumblebeee/tools/capture-on.ps1` that write the hosts file.
  - `confirm: true` — modal "Run X?" before executing. For state-
    changing ops like deploys, db drops, hosts-file edits.
- **Trust prompt for shell commands.** First time a project's
  `.firepit/config.json` contains shell-type `commands[]`, the first
  click prompts: *"Trust shell commands from `<project>`?"* with the
  full list of commands. Once approved, the file's SHA-256 is recorded
  in `state.json` `trustedCommands[]`. Any byte-level edit invalidates
  the trust and re-prompts. URL and prompt-type commands skip the gate
  entirely — they can't execute local code. Mitigates the "cloned repo
  ships malicious config" risk noted in issue #11.

### Not yet in scope (Phase B, separate release)

- `window: "reuse:<id>"` / `window: "inline"` modes — needs PTY-process
  lifecycle outside the agent session
- `longRunning: true` with a Stop-button chip — same dependency
- Prompt buttons, MCP-tool buttons, sequences, per-command icons/colors
  — listed in issue #11 as nice-to-haves

### Roadmap

- **M8: Local Ollama Sidecar** (issue #10) entered into
  `docs/ROADMAP.md` as a v0.6 target. No code yet — multi-week scope,
  intentionally deferred until V1's UX is stable day-to-day.

## [0.5.16] — 2026-05-17

### Fixed

- **Firepit's own MCP server actually works now** (issue #12). Two
  independent bugs both contributed to "Projecting 0 MCP servers" for
  every project + `/mcp` failing with opaque `-32000`:
  - The MCP host was never starting. `App.OnStartup` checked
    `Application.MainWindow` immediately after `base.OnStartup`, but WPF
    defers the StartupUri window construction to the next dispatcher
    cycle — so the property was always null and the Loaded-handler
    attachment silently no-op'd. MainWindow now calls
    `App.EnsureMcpHostStarted(this)` from its own `OnLoaded`, where the
    backend definitely exists.
  - The registry only resolved MCP ids declared in global
    `settings.json` → `mcpServers{}`. Any project that activated
    `firepit` (the meta-project's own config does this) got silently
    dropped because no user has `firepit` in their global registry —
    it's not their job to declare a built-in capability. The registry
    now seeds a built-in `firepit` entry (`command: firepit-mcp`, stdio
    transport) which users can override but don't need to declare.
  - Unknown-id activations now fire a warn callback so
    `%LOCALAPPDATA%\Firepit\logs\firepit-*.log` shows what dropped and
    why, instead of going silent.
- **Right-click context menus respect the dark theme** (issue #13).
  Implicit `Style TargetType="ContextMenu"` + `MenuItem` + `Separator`
  in the Common.xaml resource dict — same warm-dark palette as the rest
  of the chrome, hover uses the existing `#2A211A` accent. Affects
  every WPF context menu (tab strip, etc.).
- **Stripped two legacy default quick-links** that pre-v0.5.0 Firepit
  hardcoded into every settings.json (issue #14):
  `github.com/SACRVM/{projectName}` and `localhost:7180/p/{projectName}`.
  Both pointed at non-default infrastructure (maintainer's org / a
  soft-wired optional integration that needs per-project provisioning).
  The strip only removes entries whose name+url exactly match the known
  seeds — customised entries with the same names stay. A toast tells
  the user which entries were removed so they can re-add via Settings.

## [0.5.15] — 2026-05-17

### Added

- **Inbox workflow: one click, Claude processes the queue.** A new
  always-visible **Inbox** toolbar button sits between Resume and Explorer
  on every tab. Greyed out when empty; shows `Inbox (N)` and becomes
  clickable when messages arrive. Click → modal ("N Nachrichten — gemeinsam
  abarbeiten?") → on confirm Firepit hands the running Claude session a
  prompt that uses two new MCP tools, `firepit_inbox_list` and
  `firepit_inbox_complete`, to walk the queue and move each processed
  file into `.firepit/inbox/processed/`. Same outcome if you just type
  "verarbeite Inbox" — the tools are visible to Claude either way. Use
  Ctrl+C in the terminal to bail mid-walk.
- **Two-tier inbox badges.** The tab-header badge now tracks
  *new since this tab was last activated* (notification semantic — clears
  on activation), while the toolbar Inbox button tracks
  *total un-processed* (state semantic — only clears as Claude completes
  messages). Replaces the previous single badge that conflated both and
  refused to clear when clicked.

### Changed

- The tab-header inbox badge no longer launches Explorer when clicked —
  the badge is purely visual now; clicking the tab (or anywhere on its
  header, including the badge) activates it and clears the badge.

## [0.5.14] — 2026-05-17

### Fixed

- **Session restore actually restores only the active tab.** Before, a
  four-tab restore was queueing two WebView2 inits in parallel: the first
  tab's auto-select (during `Tabs.Items.Add`) would race a spurious
  follow-up `SelectionChanged` re-fire, and by the time the second event
  ran `_deferredResume` had been populated — so a non-active tab booted
  eagerly behind the active tab's ~45 s WebView2 cold start. The active
  tab's `WV2` ended up parented to a Grid that wasn't in the visual tree
  yet, and its `ready` handshake timed out. RestoreTabsFromState now sets
  a `_restoring` guard for the entire loop, `OnTabSelectionChanged` skips
  the deferred-start path while the guard is up, and the active tab is
  started by a single explicit kick at the end of restore.
- **Restart no longer leaves the console frozen.** When the user hit the
  Restart button while a session's initial WebView2 init was still in
  flight, `TeardownSessionAsync` cancelled the token but kept the
  half-built `_terminalView`. The next `StartSessionAsync` then skipped
  re-creating it (because `_terminalView` was non-null), so every PTY
  byte posted to a `CoreWebView2` that never came up — visible to the
  user as a blank, unresponsive terminal. Teardown now detects an
  uninitialised view via the new `ITerminalView.IsInitialized` flag and
  disposes it, so Rekindle always boots a fresh terminal.

## [0.5.13] — 2026-05-14

### Fixed

- **Drag-and-drop of files onto the terminal actually works now.** v0.5.8
  wired WPF `DragEnter` / `Drop` handlers onto the WebView2 — but the
  WebView2 is an `HwndHost`, and WPF's managed drag-drop never fires over
  that airspace, so dropping a file just showed the "no-drop" cursor.
  Replaced with a native OLE `IDropTarget` registered via
  `RegisterDragDrop` directly on the WebView2 host HWND. It reads the full
  paths from the `CF_HDROP` payload (which the HTML5 `drop` event can't
  expose) and pastes them into the terminal — single bare path, or
  multiple whitespace-quoted paths, as before. (Approach confirmed against
  a second opinion — this is the pattern production WebView2 apps use for
  native file paths.)

## [0.5.12] — 2026-05-14

### Added

- **Enter copies an active selection** (classic conhost QuickEdit
  behaviour). With text selected in the terminal, plain Enter copies it
  to the clipboard, clears the selection, and is swallowed — it does not
  reach the shell. Any modifier (Ctrl / Shift / Alt) lets Enter through
  to the PTY as normal. Modern Windows Terminal dropped this, but
  Firepit's audience runs on decades of conhost muscle memory and the
  selection stays visibly highlighted, so the consumed Enter isn't
  hidden state.

## [0.5.11] — 2026-05-14

### Fixed

- **The last-active tab now reliably starts on restore.** When the saved
  active tab happened to be the *first* tab in the restored list, the
  `TabControl` auto-selected it during `Tabs.Items.Add` — before the
  deferred-resume bookkeeping was populated — so the selection event was
  a no-op and the session never started. The active tab is now started
  explicitly after restore via an idempotent helper, independent of when
  the selection event fires. Other tabs still stay deferred until clicked.
- **Resize border trimmed back to 6 px.** v0.5.10's 12 px inset was
  visually heavier than it needed to be; halved it. The resize hit zone
  still works on every edge and corner — it just looks tidier.

### Added

- **Open an external shell as administrator.** Right-click the *Shell*
  toolbar button for "Open shell here" / "Open as administrator", or
  Shift+Click it for the elevated path directly. Launches Windows
  Terminal (or PowerShell) with the `runas` verb; a declined UAC prompt
  is treated as a choice, not an error.

## [0.5.10] — 2026-05-14

### Fixed

- **Window resize actually works on every edge now.** v0.5.9 widened the
  resize border but missed the real bug: the WebView2 is a child HWND, and
  `WindowChrome`'s `WM_NCHITTEST` hook on the top-level window never fires
  for pixels a child HWND covers. The terminal spanned edge-to-edge, so
  the left / right / bottom borders and both bottom corners were dead —
  only the caption-bar edges resized. Fix: inset the WebView2 by the
  resize-border width (12 px) on its three non-caption edges, exposing a
  ring at the window edge where the chrome's hit-testing works — including
  true diagonal resize from the bottom corners. The v0.5.9
  `ResizeGripDirection` corner grip is removed; it sat under the HwndHost
  in the airspace and never received input. (Approach confirmed against a
  second opinion — the alternative, subclassing WebView2's nested HWNDs,
  is fragile across WebView2 updates and navigation.)

## [0.5.9] — 2026-05-13

### Fixed

- **Window resize is no longer fiddly.** The chrome's resize border grew
  from 6 px to 10 px on every edge, and the bottom-right corner gets an
  extra 22 px diagonal grip on top of that. Grabbing the corner to resize
  now lands first-try instead of requiring pixel-perfect aim. The corner
  grip overlays the embedded WebView2 — `WindowChrome` intercepts
  `WM_NCHITTEST` before any child window sees the click, so the resize
  also works over the terminal area.

## [0.5.8] — 2026-05-13

### Fixed

- **Drag-and-drop files onto the terminal pastes the path** instead of
  opening the file in the embedded Edge layer's preview. Single files,
  multiple files, and folders are all supported; paths with whitespace
  are automatically double-quoted. Matches the Windows Terminal
  convention — useful for sharing images/files with `@<path>` references
  in Claude Code or any agent CLI.

## [0.5.7] — 2026-05-13

### Added

- **Scheduled jobs.** Per-project `.firepit/config.json` gains a
  `scheduledJobs` array — each job pairs a slash-command prompt with a
  cron expression and timezone. A headless runner spawns `claude -p` in the
  project directory, captures stdout/stderr, and writes a JSON record per
  run under `.firepit/runs/<job>/`. Failures, timeouts, and Claude's own
  usage metadata are surfaced in each record. Scheduler honours per-project
  overrides for retention, badge policy, and concurrency, and falls back to
  the platform defaults in `settings.json`.
- **Run-result badges on tabs.** A second amber pill next to the inbox
  badge shows how many run records have arrived since the user last opened
  the runs folder. Policy is `All` or `FailuresOnly` (configurable per
  project). Clicking the badge opens the runs folder in Explorer and marks
  everything as seen. Disabled globally via
  `platform.runBadgesEnabled = false`.
- **Hot-reload for job schedules.** Editing `scheduledJobs` in a project's
  config file invalidates only that project's scheduler state — no full
  restart, no cross-project disruption. Same FileSystemWatcher path that
  already powers quick-link reload.

### Changed

- **Spillover paths for oversized stdout** now default to a project-local
  `.firepit/runs/<job>/stdout-<guid>.log` so the history UI can read the
  full output without extra plumbing. The factory signature gained the full
  `JobRunRequest` so callers can override per project.

## [0.5.6] — 2026-05-13

### Added

- **`Ctrl+PgDn` / `Ctrl+PgUp` cycle tabs** as browser-style alternates to
  `Ctrl+Tab` / `Ctrl+Shift+Tab`. Same handler, just different muscle memory.

### Changed

- **`tabs.autoReloadOnConfigChange` now defaults to `true`.** v0.5.0's
  hot-reload pipeline has been running stable through v0.5.5; the explicit
  "field-test first, flip later" deferral is resolved. Quick-link edits in
  `.firepit/config.json` apply live by default. Existing user settings
  override this default as before — flip back to `false` in `settings.json`
  if you'd rather use the explicit `firepit_reload` MCP tool exclusively.

### Docs

- **README refreshed for v0.5.x** — adds the lazy-tab-restore, window
  placement, keyboard shortcut, and right-click-menu lines to the V1 core
  feature list. Status banner now points at the actual latest release.

## [0.5.5] — 2026-05-12

### Added

- **Window position and size persist across restarts.** Move or resize the
  Firepit window, close it, reopen — it comes back where you left it.
  Maximized state is preserved too (the un-maximized rect is also saved so
  restore-down lands at the right place). State schema gains a nullable
  `window` field; legacy `state.json` files without it fall back to
  CenterScreen + 1180×700 (the previous default). Off-screen rects (e.g.,
  laptop returned from a disconnected dock) are silently ignored.

## [0.5.4] — 2026-05-12

### Added

- **Tab keyboard shortcuts** — `Ctrl+Shift+T` opens the project picker,
  `Ctrl+Shift+W` closes the active tab, `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle
  forward/back, `Ctrl+Alt+1..9` jump to tab N. Shift-prefixed variants
  (instead of plain `Ctrl+W`/`Ctrl+T`) avoid stomping on bash readline's
  delete-word and transpose-chars bindings inside the embedded terminal.
- **Tab right-click menu** — Close, Close others, Close all. Common
  terminal-app UX; each item runs through the same close path with the
  Burning-session confirmation prompt.

### Changed

- **Closing a Burning tab now asks for confirmation** instead of killing the
  agent silently. Mirrors the existing Rekindle-confirm UX. Embers, Dead,
  and Igniting tabs still close without a prompt.

## [0.5.3] — 2026-05-12

### Changed

- **Restored tabs load lazily — only the previously-active tab starts at
  launch.** Other restored tabs sit cold (no spinner, no PTY) until the user
  clicks them. The first click triggers init and shows the spinner. Cuts
  startup CPU + memory roughly in proportion to tab count, and the tab the
  user last had focused is the one Firepit prioritises. State schema gains a
  new `activeTabProjectName` field (nullable, backwards-compatible).

## [0.5.2] — 2026-05-12

### Changed

- **`.firepit` meta-project always pins to top of the project picker** so the
  cross-project hub is one click away regardless of manual entries or alpha
  order. Other discovered projects stay alphabetical; manual entries keep
  their relative ordering after the pin.

### Fixed

- **GitHub quick-link icon now resolves to the Octocat** instead of the
  generic chain-link fallback. Root cause: resource-key case mismatch
  (`IconGitHub` vs the `Capitalise()`-normalised lookup `IconGithub`).
- **Personal GitHub/Fishbowl URLs removed from `FirepitSettings.Defaults`.**
  Previously the defaults shipped with `github.com/SACRVM/{projectName}`
  and `localhost:7180/p/{projectName}` — author-specific config that
  shouldn't have leaked into the OSS defaults. QuickLinks now start empty;
  configure via `settings.json` globals or per-project `.firepit/config.json`.
  Existing user settings are untouched.

## [0.5.1] — 2026-05-12

### Fixed

- **Installer adds Firepit to user PATH** so `firepit-mcp` is resolvable from any
  shell or ConPTY child without manually editing `settings.json`. Opt-out
  available as a wizard task. Removed cleanly on uninstall. Closes #3, #6.
- **MCP spawn failures now surface in the workspace tab.** Pre-flight PATH
  resolution runs at session start; missing-binary failures show a non-modal
  banner ("⚠ MCP server failed: `<id>` — `<command>` not found on PATH"),
  click to dismiss. Closes #4.
- **Meta-project no longer creates a dead root-level `inbox/`** — actual inbox
  traffic has always gone to `.firepit/inbox/`. Templates (CLAUDE.md, README.md,
  .gitignore) updated to match. Existing meta-projects auto-clean an empty
  legacy `inbox/` on next bootstrap; non-empty ones are left alone. Closes #5.

### Added

- **"Configure" toolbar button** opens the project's `.firepit/config.json`,
  scaffolding a commented JSONC template if missing. Lowest-friction entrypoint
  to the per-project config surface. Closes #9.

### Docs

- **`docs/V1.12-INSTALLER.md` v0.2** — adds the MCP-bridge resolution decision
  and a "first-run end-to-end check" template for future planning docs.
- **`docs/ARCHITECTURE.md` v0.3** — fixes §13/§14 subsection numbering, adds
  the V1.1.4 `progress` bridge message, corrects the `ProjectMcpActivation`
  schema, adds §9.7 (MCP lifecycle errors, symmetric to §4.4), and documents
  `firepit-mcp.exe` in §13 Distribution. Closes #7.
- **`SPEC.md` v0.3** — original V1 vision preserved; tech-stack (`Pty.Net` →
  `Porta.Pty`), architecture diagram, and configuration sections updated in
  place. New "Shipped — what v0.5.0 added beyond this spec" section enumerates
  meta-project, MCP bridge, inbox, commands, hot-reload, OSC 9;4, V1.2 tab
  interactions, and the V1.12 installer. Closes #8.

## [0.5.0] — Firepit as Platform — 2026-05-11

The biggest shift since V1: Firepit becomes a meta-workspace. Per-project
config lives next to your code, Claude can talk to Firepit through MCP, and
a hidden `.firepit` central project becomes your hub for cross-project
work.

### Added

- **Per-project `.firepit/config.json`** — quick-links, MCP activations,
  agent overrides, and session env now live alongside the project. The
  file travels with your repo; gitignore at your discretion. Resolution
  order: defaults → global `settings.json` → per-project file (per-project
  wins).
- **Silent migration** — first launch after upgrade walks
  `settings.Projects[]` and splits behavioural fields out into per-project
  files. Toast confirms; `settings.json.bak` archived.
- **Hot-reload pipeline** — quick-link edits in `.firepit/config.json`
  apply live; MCP / agent / env changes surface a "restart needed" banner
  in the tab toolbar. Optional `tabs.autoReloadOnConfigChange` flag enables
  a debounced `FileSystemWatcher` (off by default).
- **Firepit MCP server** — Claude Code can call `firepit_*` tools to list
  / open / focus / close / reload tabs, and read `firepit://projects`,
  `firepit://sessions`, `firepit://settings` (secrets redacted).
  Architecture: stdio bridge `firepit-mcp.exe` ↔ named pipe ↔ in-process
  GUI host. Up to 8 concurrent client connections.
- **`.firepit` meta-project** — first-launch prompt seeds a hidden
  central project at the projects root (`CLAUDE.md`, `README.md`,
  `.claude/settings.json` preregistering the firepit MCP, `notes/`,
  `inbox/`). Your hub for cross-project knowledge and orchestration.
- **Cross-Claude inbox** — `firepit_send_to(toProject, subject, body)`
  drops a markdown message into `<toProject>/.firepit/inbox/`. Receiving
  project's tab shows an unread-count badge; click opens the inbox folder.
  `FIREPIT_PROJECT_NAME` env var injected at PTY spawn so the bridge can
  populate the `from` field automatically.
- **Custom commands** — `commands[]` in `.firepit/config.json` adds
  toolbar buttons. Three types: `shell` (spawns a process in the project
  dir), `claude-prompt` (injects text into the active session as if you
  typed it), `url` (opens in browser).
- **Icon flexibility** — bundled curated set extended from 8 → 28 named
  icons (Lucide-style minimalist). Inline SVG path-data is also accepted
  (`"icon": "M2 2L10 10z"`) — WPF's mini-language is ~99% SVG-path
  compatible, so most single-path SVGs paste in directly.
- **Tab-switch focus** — switching tabs hands focus directly to the
  embedded terminal. Type immediately, no click.
- **`docs/PLATFORM.md`** — implementation reference for v0.5.0+.
- **`docs/FISHBOWL.md`** — Fishbowl-as-canonical-MCP-integration writeup
  (one team per Firepit project, bearer-per-project pattern).

### Changed

- Versioning convention: doc names drop the `V1.x.y` pattern. New docs
  are named by feature (`PLATFORM.md`, `FISHBOWL.md`) and carry a
  `**Target:** vX.Y.Z` front-matter. Existing `V1.11/12/13`-style docs
  stay as historical artifacts.
- `SPEC.md` MCP examples updated from the older `X-Project` header
  pattern to the bearer-per-project pattern that real adapters use.

### Deferred

- Interactive approval UX for MCP mutations — every tool call is
  currently auto-allowed and logged. `state.json` already carries a
  `ToolApprovals` slot for the prompt-with-memory flow that lands later.
- Settings dialog UI for the new `Platform.*` knobs (hand-edit
  `settings.json` for now).
- Per-message threading / reply UX in the inbox.
- Auto-launch of Firepit GUI when the bridge is invoked but the GUI
  isn't running (today: clear error to Claude).

## [0.4.0] — Reliability + Tab Interactions — 2026-05-10

Two minor doc-tracks (V1.1.4 reliability fixes + V1.2 tab interactions)
shipped together.

### Added

- **Drag-to-reorder tabs** — 8 px hysteresis triggers a drag adorner;
  drop indicator (2 px brand-warm bar) snaps to the nearest gap. Order
  persists across restarts.
- **Terminal search** — `Ctrl+Shift+F` opens an in-tab overlay backed by
  `xterm-addon-search`. Live-search on input, `Enter` / `Shift+Enter`
  navigate, `Esc` closes. Each tab has its own search state.

### Fixed

- **Multi-tab cold-start race** — parallel WebView2 inits under load
  exceeded the 15s ready-handshake timeout for the 4th+ tab, leaving
  zombies that silently dropped keystrokes. WebView2 boot serialised
  through a static `SemaphoreSlim`.
- **Agent activity during thinking** — `ActivityDetector` now hooks
  Claude Code's `OSC 9;4` (ConEmu / Windows Terminal tab-progress)
  emissions via xterm.js's `parser.registerOscHandler`. Tab stays gold
  while the agent thinks even though no bytes are flowing.
- **Maximize chrome clipping** — WPF custom-chrome windows extend their
  client rect past the work area by `ResizeBorderThickness` when
  maximized. Compensated with a matching margin on the root grid; the
  yellow accent on the selected tab is fully visible again.

## [0.3.0] — V1.13 Font Scaling — earlier

- Single `ui.fontSize` setting cascades through caption, tabs, toolbar,
  dialog rows, and the embedded terminal via the bridge.

## [0.2.0] — V1.12 Installer — earlier

- Inno Setup installer with projects-root pre-seed marker.
- `/release` slash command + Version field as source of truth.

## [0.1.0] — V1 GA — earlier

M0–M8 milestones. See `docs/ROADMAP.md`. Functional translation: open
Firepit, see your projects, click each to summon Claude, run real
sessions in parallel, close + reopen and resume seamlessly.

[Keep a Changelog]: https://keepachangelog.com/en/1.1.0/
