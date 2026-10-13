# Firepit on Linux

**Target:** v0.31.0 (headless platform)
**Status:** decided 2026-10-13 — M10 done, M11 next

## Decisions (2026-10-13)

- **Claude Code desktop is the UI.** Firepit stops being a window and becomes
  what the desktop app lacks: the platform behind MCP (knowledge, inbox,
  artifacts, jobs, blueprints) and the project commands. Its own shell is
  optional and only comes back if it is missed in daily use (⛳2). If it does,
  it is the "cockpit": the real TUI in the middle, structured panels around it
  fed by Claude Code's hooks and statusline (see the hybrid notes below).
- **Project commands live in Claude Code**, as the `firepit-commands` mod: a
  button bar above the prompt (`Run:` site ticker  `Git:` status repo …), a
  panel with descriptions, logs and output, and `/fire`. Long-running
  commands run as systemd user units, windows open in Konsole, a trust gate
  guards shell commands. The reference project is `../firepit-template`.
- **The name stays.** A firepit is where the projects gather; the shell
  vocabulary (cold, igniting, burning, embers) retires with the shell.
- **main is Linux-first.** v0.30.0 is the last Windows release; the WPF code
  lives on the `windows-legacy` branch. On main, `src/Firepit` and
  `src/Firepit.Web` are out of the solution and leave once M11 has moved their
  logic out.
- **M11 shrinks** to the backend extraction. The session controller is only
  needed for a shell of our own.
- **The bridge finds its project by the session's working directory**:
  Claude Code desktop exports no `FIREPIT_PROJECT_NAME`.

## Context

The author is moving from Windows to CachyOS (KDE Plasma 6, Wayland). Firepit
v0.30.0 is Windows-only: WPF, WebView2, ConPTY, Credential Manager, named
pipes, Inno Setup. Two things break immediately, before any question of a GUI:

1. **Every Firepit-wired repo loses its tools.** 47 repos under
   `~/Projects/repos` reference `firepit-mcp` in `.mcp.json`; 51 carry a
   `.firepit/` folder; 315 knowledge documents exist. On this machine
   `firepit-mcp` does not exist, so `firepit_knowledge_*`, `firepit_inbox_*`,
   `firepit_artifact_*` are dead in every Claude session — including Claude
   Code desktop, which reports "Executable not found in $PATH: firepit-mcp".
2. **The MCP server cannot run without the WPF window.** `MainWindow` is the
   only `IMcpBackend`; `firepit-mcp` is a dumb stdio↔named-pipe tunnel into
   the GUI process. No window, no knowledge base.

So the first deliverable is not a shell — it is the platform, headless.

## Where the code stood (measured on Linux, 2026-10-12, before M10)

`dotnet test` of the portable projects on CachyOS, .NET SDK 10.0.112
(`global.json` pins 10.0.203; run in a copy with a relaxed pin):

| Project | TFM | Linux result | Failures are about |
|---|---|---|---|
| Firepit.Core | net10.0 | 368 / 383 | `\` separators, `C:\`/UNC test data, case-insensitive paths, PATHEXT, Windows-invalid filename chars |
| Firepit.Adapters | net10.0 | 13 / 13 | — |
| Firepit.Knowledge | net10.0 | 67 / 106 | `vec0.dll` hardcoded (package ships `linux-x64/vec0.so`), Windows file-share locking semantics, path separators |
| Firepit.Mcp | net10.0-windows + UseWPF | not built | nothing — `McpHost` uses no WPF; the TFM is a leftover |
| Firepit.Process | net10.0-windows | not built | only the TFM and Credential Manager P/Invoke — Porta.Pty 1.0.7 already ships a Linux `forkpty` shim (`libporta_pty.so`) and Firepit uses only its cross-platform API |
| Firepit.Web | net10.0-windows + WPF | rewrite | WebView2, OLE `IDropTarget` |
| Firepit (shell) | net10.0-windows + WPF | rewrite | WPF, Win32 focus/chrome, Inno updater |

Roughly 29k lines of source. The portable libraries really are portable.
The trouble is what sits in the shell (next section).

## Review

### What held up

- **The invariants did their job.** `ITerminalView` and `IPtyChannel` exist;
  Core/Adapters/Knowledge never reference WPF; the C# side never parses PTY
  output (OSC 9;4, OSC 52 and link detection live in `terminal.html`).
- **Core is well tested** (~500 test methods across Core/Adapters/Knowledge/
  Process) with real seams (`IActivityClock`, `IPtyChannel`).
- **Failure-mode thinking is excellent** — the CHANGELOG reads like an
  incident log written by someone who cares (Storage Sense deleting `vec0.dll`,
  degraded search instead of silent nothing, scratch-file index rebuilds).

### What drifted

1. **`MainWindow` is the application.** Of ~8,800 lines in the shell's main
   files, ~5,600 are business/orchestration logic with **zero tests**:
   project registry, settings mutation, watchers, job scheduler, knowledge
   scope registration (`SyncKnowledgeScopes`, 220 lines), integrity, blueprints,
   rename-with-history-migration, inbox delivery — and the whole
   `IMcpBackend`. This is the single biggest blocker for Linux *and* for
   headless use. About 2,700 of those lines need nothing from WPF except
   `Dispatcher` marshalling.
2. **`SessionTab` is the session controller**, not a view: PTY lifecycle,
   restart/resume state machine, MCP projection, prompt injection, command
   execution behind the trust gate. It holds the concrete
   `WebView2TerminalView` (uses `.Element`, `.CurrentSize`, which aren't on
   `ITerminalView`) and calls `ConPtyLauncher` statically. Invariant #1 holds
   by the letter, not the spirit. `Core/Sessions/Session.cs` is an unused stub
   — the object `SessionTab` should have been.
3. **Duplication.** Inbox listing/completion exists three times
   (`InboxWindow`, `MainWindow.Mcp`, `InboxDelivery.PendingIds`). Pipe-name
   rules are duplicated between Core and the bridge.
4. **Bugs found in passing** (worth fixing regardless of platform):
   - `claude-prompt` commands write `prompt + "\n"` in one write
     (`SessionTab.cs:1204`) — the exact paste-coalescing trap that
     `SubmitPromptAsync` documents and fixes. Likely lands in the input box
     without submitting. `RunShellInline` (`:1403`) does the same with `\r`.
   - MCP `firepit_close_tab` skips `_resumableProjects.Add` and
     `ScheduleStateSave` that the UI close does (`MainWindow.Mcp.cs:231`).
   - Rename carries the resume flag by **name**
     (`MainWindow.ProjectRename.cs:216`) but the set is keyed by **path**.
   - Settings changes rebuild quick-links/MCP registry, but open tabs keep the
     old instances (`SessionTab.cs:43-44`).
   - Artifacts button tooltip promises `Ctrl+Shift+A`, which is not bound;
     the inbox badge tooltip says "click to open folder", no handler.
   - Dead code: Core `Session`, `ProjectListView`, `InboxWindow.HasMessages`,
     `IMcpBackend.DeleteInboxMessageAsync`, `SingletonCommand.Summon` (the
     receive side exists, nothing sends it).
5. **The knowledge layer is the complexity hotspot.** `KnowledgeService` is
   1,324 lines; storage modes (repo / self / central store / shared base /
   pointer file / aliases) and their cross-checks account for a large share of
   the 0.13–0.29 fixes. For one user that is a lot of failure surface. During
   the port, freeze the surface; afterwards ask whether every mode still earns
   its keep.
6. **The docs no longer describe the product.** CLAUDE.md lists "markdown
   viewer, image viewer" as out of scope — v0.28 shipped exactly that.
   "Not a control plane" — blueprints, integrity, jobs and a cross-project
   inbox are a control plane, just a local one. That's a fine product; the
   brief should say so.
7. **Distribution weight.** `Firepit.exe` ~160 MB and `firepit-mcp.exe`
   ~70 MB self-contained, the latter for a tunnel that copies bytes.

### Windows assumptions inside the "portable" code

- Paths compared `OrdinalIgnoreCase` everywhere (`_openTabs`,
  `_resumableProjects`, `PathsEqual`, knowledge maps) — wrong on a
  case-sensitive filesystem.
- **Knowledge pointer files use backslashes: 21 of 22 in `~/Projects/repos`**
  (`..\..\.firepit\projects\<name>\knowledge`). On Linux `\` is a filename
  character; every redirected scope breaks.
- **Shell-command trust is matched on project path case-insensitively**
  (`CommandsTrustLedger.cs:55,68,83`) — on Linux `/x/Foo` and `/x/foo` are
  different projects that would share trust. Security-relevant.
- Environment dictionaries are `OrdinalIgnoreCase` (PTY env, MCP env merge);
  env names are case-sensitive on Linux.
- `vec0.dll` hardcoded in `SqliteVecExtension.cs:46` — on Linux search would
  silently fall back to full-text only.
- `FileSystemWatcher` filters (`*.md`, `*.json`) become case-sensitive.
- Defaults: `projectsRoot` = `~/SynologyDrive/PROJECTS`, `shells.preferred`
  = `wt`; the updater only matches `FirepitSetup*.exe`.
- `McpHealthChecker` resolves commands via PATHEXT.
- `JsonJobHistoryStore.SanitizeJobName` relies on Windows-invalid characters.
- Tests that assert Windows sharing violations (`NonBlockingFile`): on Linux
  a reader never blocks a writer, so the problem they guard against doesn't
  exist there — make those cases Windows-only.

## Firepit vs. Claude Code desktop

Claude Code desktop (the Code tab) runs natively on Linux now. Honest
comparison, because it decides how much shell is worth building.

| | Firepit | Claude Code desktop |
|---|---|---|
| Agent fidelity | the real CLI TUI — every slash command, `/config`, `/permissions` | GUI over the agent; terminal-dialog commands unavailable |
| Agents | any CLI (Claude, Aider, Codex…) or a plain shell per tab | Claude only |
| Session overview | tabs + activity states inferred from PTY timestamps + OSC 9;4 | sidebar, groups, unread, split view; state known exactly (waiting for permission / done) |
| Project model | projects root auto-discovery, picker, per-project toolbar commands & quick-links with trust gate | folders/sessions, no project toolbar |
| MCP config | global registry, per-project activation, `${env:}`/`${cred:}` secrets, projected into `.mcp.json` | `.mcp.json` + connectors |
| Cross-project | persistent file inbox with auto-delivery into idle sessions; shared semantic knowledge base (FTS + local ONNX vectors) across 51 repos; blueprints & CLAUDE.md fragments; integrity checks | per-project memory (files, no vector search); messages to *running* local sessions |
| Code review loop | external editor | diff pane, worktrees per session, PR/CI monitoring with auto-fix, browser preview of dev servers |
| Scheduling | per-project cron in `.firepit/config.json`, run history in repo | scheduled tasks in the app |
| Sharing / mobile | none | hosted artifacts, remote control, cloud sessions |
| Ownership | open, local, hackable — and every quirk is yours to fix | closed, account-bound, zero maintenance, ships weekly |

**Reading.** The shell half of Firepit (tabs, activity, overview) is where
Claude Code desktop has caught up and in places passed it. The platform half —
knowledge, inbox, blueprints, integrity, jobs, MCP registry — has no
equivalent there. And it already reaches Claude Code desktop, because it
speaks MCP. **Firepit's lasting value is the platform; the shell is
the part to justify.** Roughly a quarter of the 75 CHANGELOG fixes are
shell/Windows quirks (focus theft, caption chrome, DirectWrite fonts,
WebView2 init races) — that cost doesn't come back on Linux, but a new
toolkit brings its own.

## Decision

**⛳1 Port order:** platform first, headless (M10–M12). It's no-regret: any
future shell needs the same extraction, and it restores the tools in all 47
repos within days, in every client.

**⛳2 After two weeks of use** (Claude Code desktop or the CLI in Konsole,
plus headless Firepit): is the shell still missed? If not, stop — Firepit
becomes a headless platform with a CLI, and the next steps are optional.

**⛳3 a) vs b) — answer: Linux-first on a portable stack.** The toolkit, not
the number of platforms, decides the cost:

| Option | Toolkit | Terminal | Reuse | Risk |
|---|---|---|---|---|
| **A — Avalonia 12** (recommended default) | XAML close to WPF; MIT; native Wayland opt-in since 12.1 | xterm.js in `Avalonia.Controls.WebView` (MIT; WPE or WebKitGTK on Linux) — `terminal.html` and the bridge protocol carry over | views port, terminal page unchanged, Windows stays buildable for free | embedded WebKit on KDE Wayland: focus, IME/dead keys, latency — **spike first** |
| B — GTK4/libadwaita via Gir.Core | code or Blueprint UI, no XAML | **VTE** (`vte4` 0.84 in the repos) — the Ptyxis/GNOME Console widget; OSC 9;4 via termprops since 0.80 | views rewritten; terminal features re-done on VTE APIs; no Gir.Core VTE package — bindings must be generated | binding work; GNOME look on KDE; Linux-only |
| C — no shell | Konsole/kitty tabs + `firepit open <project>` | the emulator's own | none needed | lose the one-window workspace and toolbar |

"Linux-only" is only cheaper with option B, and B is the expensive one.
With A, cross-platform costs almost nothing extra — so build A, but treat
Linux as the only tested and released target: no Windows CI, no Inno, WPF
frozen at v0.30.x. If the M13 spike shows xterm.js in an embedded WebKit
is janky on KDE Wayland, switch to B; don't fight the web view.

## Hybrid notes (only if the shell comes back)

The TUI stays the interaction surface; a GUI around it carries the
information. Fed by what Claude Code publishes on purpose — never by reading
the terminal, so invariant #2 holds:

- **Hooks**, injected per session with `claude --settings <file>` (nothing
  written into the repo; re-passed on every resume). `PermissionRequest`
  fires the moment a permission dialog shows and may answer it (an Allow
  button in the overview), `UserPromptSubmit`/`Stop` bracket a turn,
  `TaskCreated`/`TaskCompleted` give the task list, `PostToolUse` the files
  touched. `http` hooks post straight to the daemon.
- **Statusline JSON**: model, context use, cost, lines changed, rate limits.
  A `--settings` statusline replaces the user's own, so it chains to it.
- **Not** the transcript JSONL — its format is internal and changes between
  releases.
- One web view for the whole window (xterm.js plus HTML panels), a thin
  native host; WebKitGTK on KDE Wayland is the risk the spike checks.

## Milestones

Tags continue the ROADMAP scheme: `m{N}-{slug}`.

### M10 — Portable core

- `global.json`: lower the pin to `10.0.100` (keep `latestFeature`) — Arch
  ships the 10.0.1xx band, the current pin demands 10.0.203 and roll-forward
  never goes down.
- `PathComparer` in Core: `Ordinal` on Unix, `OrdinalIgnoreCase` on
  Windows; replace every path-keyed `OrdinalIgnoreCase`.
- Knowledge pointer files: read either separator, always write `/` (Windows
  accepts it). `firepit_integrity_check repair=true` rewrites old pointers.
- `SqliteVecExtension`: resolve `vec0.{dll,so,dylib}` by OS.
- `FirepitPaths`: XDG — config `~/.config/firepit`, state
  `~/.local/state/firepit` (state.json, logs), data `~/.local/share/firepit`
  (natives, index caches), cache `~/.cache/firepit` (ONNX model).
- `Firepit.Mcp` → `net10.0`, no WPF. `Firepit.Process` and its tests →
  `net10.0`; Credential Manager behind `OperatingSystem.IsWindows()`.
  Porta.Pty 1.0.7 stays (2.x is optional). Check process-tree kill: on Unix
  `Kill()` signals only the child, so MCP servers spawned by `claude` may
  outlive the tab — kill the process group.
- Path-keyed comparisons through the new comparer, the trust ledger first.
- `firepit-mcp`: drop the hardcoded `win-x64` RID; replace the
  `\\.\pipe\` existence probe.
- Secrets: `LibSecretProvider` for `${cred:firepit/<key>}` (Secret Service;
  KWallet answers it on Plasma 6). `${env:}` unchanged.
- Fix the test data that is Windows by nature (`C:\`, UNC) — run those
  cases on Windows only and add Unix equivalents.
- CI: an `ubuntu-latest` job for Core/Adapters/Knowledge/Process/Mcp.

**Accept:** all portable test projects green on Linux; PTY smoke test spawns
`claude --version` and `bash` via Porta.Pty 2.x on Linux.

**Done (2026-10-13):** 512 tests pass on Linux, 4 Windows-only facts skipped
(`[WindowsFact]`). Porta.Pty 1.0.7 stays — its forkpty shim works; reading a
pseudo-terminal whose child exited fails with EIO on Linux, now treated as
end of stream. Paths compare through `PlatformText` (Core) / `PathText`
(Knowledge). Data lives in `~/.config/firepit`, `~/.local/share/firepit`,
sockets in `$XDG_RUNTIME_DIR/firepit`. `${cred:…}` resolves through
`SecretServiceSecretProvider`. CI runs on ubuntu-latest.

### M11 — Workspace service

Extract the application out of `MainWindow` into a portable
`Firepit.Workspace` (net10.0):

- Owns project registry, settings mutation, config/inbox/runs watchers, job
  scheduler, knowledge scopes, update check, and **implements `IMcpBackend`**.
  Moves the Mcp, Knowledge, Blueprints, Integrity, ProjectRename and
  InboxDelivery partials nearly verbatim.
- Talks to any UI through a narrow `ITabHost` (Open, Focus, Close,
  ListSessions, ApplyConfig, RefreshArtifacts, IsUserTab) and a
  `SynchronizationContext` instead of `Dispatcher`.
- `SessionController` (portable) carved out of `SessionTab`, behind an
  `IPtyLauncher` seam and an `ITerminalView` extended with `CurrentSize`.
  The view only renders state and affordances.
- One inbox implementation, used by MCP, window and auto-delivery.
- Lift-and-shift: `CommandRunner` → Process (process-group kill on Unix),
  `SingletonGuard`, `FileSystemProjectConfigWatcher` → Core.
- Fix the bugs listed in the review while the code is in hand.

**Accept:** the WPF shell still builds on Windows against the extracted
service (a sanity build, not a release); workspace service has tests for
registry, inbox, MCP backend calls against a fake `ITabHost`.

### M12 — Headless Firepit on Linux

- `firepitd`: hosts the workspace service without UI. Transport: Unix
  domain socket at an explicit path, `$XDG_RUNTIME_DIR/firepit/mcp.sock`
  (named-instance suffix as today), mode 0600, same length-prefixed framing
  and context handshake. Don't rely on `NamedPipeServerStream` on Unix: it
  maps to `$TMPDIR/CoreFxPipe_<name>`, so host and bridge must agree on
  `$TMPDIR`, and without `CurrentUserOnly` the socket follows the umask.
- **Single instance must be redone.** `SingletonGuard` relies on a second
  `NamedPipeServerStream` throwing; on Unix the second process unlinks and
  rebinds the socket instead, so two instances run side by side, and a
  crash leaves a stale socket. Use `flock` on a lock file plus stale-socket
  cleanup.
- `firepit-mcp` bridge on Unix sockets; publish framework-dependent or
  NativeAOT (a few MB instead of 70).
- **systemd user units:** `firepit.socket` + `firepit.service` — socket
  activation starts the daemon on the first MCP connect; the job scheduler
  runs in it.
- Tab-bound tools (`open_tab`, `focus_tab`, `close_tab`, `list_sessions`)
  answer "no shell running" headless. Inbox auto-delivery needs a PTY, so
  headless it degrades to pending-badge semantics.
- `firepit` CLI: `firepit open <project>` (opens a Konsole tab in the project
  with the agent), `firepit inbox`, `firepit status`.
- Install into `~/.local/bin` **and** make it visible to GUI-launched apps:
  `~/.config/environment.d/10-local-bin.conf` with
  `PATH=$HOME/.local/bin:$PATH`. Today the systemd user PATH holds
  `~/.dotnet/tools` but not `~/.local/bin` — the same gap will hide a
  natively installed `claude`. (Alternative: ship `firepit-mcp` as a
  .NET global tool, which lands in `~/.dotnet/tools`.)
- Shell singleton later takes over from the daemon (handover command), so
  there is one workspace host at any time.

**Accept:** in Claude Code desktop and in the CLI, in any of the 47 repos,
`firepit_knowledge_search` (hybrid, not degraded), `firepit_inbox_list`,
`firepit_send_to`, `firepit_artifact_add` work; a scheduled job fires
from the daemon; reboot → first MCP call starts it again.

Tag `v0.31.0`. **⛳2 here.**

### M13 — Shell spike (2–3 days, throwaway)

One window, two tabs, Claude Code running in each, on KDE Wayland at 125 %
scaling — once with Avalonia + WebView + `terminal.html`, once with GTK4 +
VTE if the first fails. Checklist:

- Claude Code TUI renders correctly (colours, box drawing, emoji, wide chars)
- typing latency indistinguishable from Konsole; German layout: AltGr,
  dead keys (`^ ´ \``), umlauts; `Ctrl+Shift+*` shortcuts reach the app
- 100 kB paste; resize storms; six tabs streaming at once
- focus moves cleanly between tab strip, toolbar and terminal
- OSC 9;4 drives the activity state (unverified: does Claude Code emit it
  under a Linux `TERM=xterm-256color`? If not, activity falls back to
  timestamps alone)

**Accept:** a written verdict with the chosen toolkit. ⛳3 confirmed.

### M14 — Linux shell MVP

Tabs, project picker, terminal, activity states, state restore (lazy),
single instance, settings dialog, toolbar with Linux semantics:

| Windows | Linux |
|---|---|
| Open in Explorer | `xdg-open <dir>`; reveal file via `org.freedesktop.FileManager1.ShowItems` (Dolphin) |
| Open External Shell (`wt`/`powershell`) | `shells.external`, default `konsole --workdir {path}`, else `$TERMINAL` |
| Run as administrator | dropped (use `sudo` in the terminal) |
| `cmd.exe /c` + PATHEXT | `$SHELL -lc` |
| `taskkill /T` | kill the process group |
| Inno self-update | see M16 |

Agent environment: GUI-launched apps don't see the login shell's PATH.
Resolve it once at startup (`$SHELL -lic env`) and pass it to the PTY.

**Accept:** the author runs three projects in it for a week instead of
Konsole tabs.

### M15 — Parity

Inbox window, artifacts pane + viewer (same web view, Markdig HTML), project
settings dialog, commands with trust gate, drag & drop of files and images
into the terminal, update badge.

### M16 — Packaging & retirement

- `dotnet publish -r linux-x64` (framework-dependent; `dotnet-runtime` comes
  from pacman) into `~/.local/opt/firepit`, symlinks in `~/.local/bin`,
  `.desktop` entry + icon, systemd units. A `PKGBUILD` for `makepkg -si` is
  the Arch-native alternative.
- Update path: `GitHubUpdateChecker` stays; the installer is replaced by
  "download tarball, swap directory, restart" (Linux allows replacing a
  running binary by rename).
- Retire WPF: tag `v0.30.x` as the last Windows release; move `src/Firepit`
  (WPF), `Firepit.Web` (WebView2), `installer/`, `run.ps1` and the Windows
  CI to a `windows-legacy` branch; update CLAUDE.md, SPEC, ARCHITECTURE
  (invariant #3 becomes "a real pseudo-terminal: ConPTY on Windows, openpty
  on Unix").

Tag `v0.40.0`.

## Data migration (part of M10/M12)

| What | Action |
|---|---|
| `%APPDATA%\Firepit\settings.json` | copy to `~/.config/firepit/`; rewrite `projectsRoot` (`D:\repos` → `~/Projects/repos`) and manual entries |
| 21 knowledge pointer files with `\` | normalised on read (M10); rewrite via integrity repair, commit per repo |
| `.firepit/config.json` commands | 17× `powershell`, 14× `pwsh`, 11× `cmd`, 7 absolute `C:\`/`D:\` executables (Unity, AfterMatch builds, devenv) — integrity check should flag Windows-only commands; rewrite by hand (or add an optional `os` filter per command if Windows comes back) |
| `.firepit/artifacts.json` | absolute `D:\dev\…` entries show as missing — unpin |
| `${cred:…}` secrets | re-enter into Secret Service (`secret-tool store --label="Firepit: <target>" service firepit target <target>`) |
| `knowledge.db` indexes, ONNX model | regenerate (reindex from Markdown; model re-downloads) |
| `~/.claude/projects` history | Windows sessions don't carry over; "Resume Last" starts fresh once — not worth migrating |

## Prerequisites on this machine

- `claude` CLI is not installed (only the desktop app) — needed for the shell
  and for scheduled jobs.
- Node.js is not installed — `Firepit.Web` runs `npm ci` at build time for
  the xterm.js bundle (or vendor the bundle and drop the npm step).
- `global.json` pin vs. installed SDK (see M10).
- Option B only: `pacman -S vte4`.
