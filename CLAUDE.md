# CLAUDE.md

Project guidance for Claude Code sessions working on Firepit. This file is the operational brief — what to build, what not to build, and which lines must not be crossed.

## Git workflow (durable authorization)

This is a single-developer repo. The following git operations are **authorized in advance** for the Claude Code agent — no per-action confirmation needed:

- **Direct push to `main`** — PRs are not required. Commit, push, move on.
- **Push of `v*` release tags and `m*` milestone tags** — when the work justifies a release, tag and push without ceremony.
- **Branch deletion** (local + remote) for cleanup of merged feature/release branches.

Still gated (require explicit per-action confirmation):

- `git push --force` / `--force-with-lease` to `main`
- `git reset --hard` on `main`
- Tag deletion or moving an existing tag
- Anything destructive to history

## Reading order

1. **`SPEC.md`** — product vision, scope, brand language. The *why*.
2. **`docs/ARCHITECTURE.md`** — technical contract: interfaces, bridge protocol, threading, security boundary, distribution. The *how*.
3. **`docs/ROADMAP.md`** — V1 milestones M0–M7, ordered, each with deliverables and acceptance criteria. The *when* and the working plan for autonomous execution.

For any V1 implementation work, `ROADMAP.md` is the working plan. Pick the next unfinished milestone, deliver it against the listed acceptance criteria, tag it (`m{N}-{slug}`), move on.

---

## What Firepit Is

A native Windows shell that hosts AI agent CLI sessions (Claude Code, Aider, etc.) in tabs, with activity indicators and a project switcher. Local-first, terminal-centric. **Not** a cloud orchestrator, **not** an IDE, **not** an Electron app.

If you find yourself building a file manager, an editor, or a control plane — stop. That is not this product.

---

## Tech Stack (V1)

| Layer | Choice |
|---|---|
| Runtime | .NET 10 |
| UI Shell | WPF (native) |
| Terminal Tile | WebView2 + xterm.js |
| PTY Backend | Pty.Net (or direct ConPTY P/Invoke) |
| Config | JSON in `%APPDATA%\Firepit` |

Platform: **Windows-only** in V1. Cross-platform is deferred.

---

## Architectural Invariants

These are non-negotiable. Violating them is a regression even if tests pass.

1. **`ITerminalView` is the single contract** between tab UI and terminal renderer. V1 ships only `WebView2TerminalView`, but the abstraction must exist from day one. No code outside the WebView2 implementation may import `Microsoft.Web.WebView2` types or know about xterm.js.
2. **Firepit is a transparent host.** It does not parse, modify, or interpret PTY output beyond timestamping reads for activity detection. No screen-scraping the agent's output.
3. **Each agent runs as a real child process under ConPTY.** Stdin/stdout redirection without a pseudo-console will not work for TUI agents and is forbidden.
4. **Brand vocabulary is user-facing only.** Code uses neutral identifiers (`Project`, `Session`, `Tab`, `TerminalView`, `ProcessHost`, `ActivityDetector`). Logs are professional. *Burning*, *embers*, *summon*, *rekindle*, *gather* belong in tooltips, status strings, error messages, README — never in class names.
5. **Project layout splits UI from logic:** `Firepit.Core`, `Firepit.Process`, `Firepit.Adapters` must not reference `Firepit` (the WPF project). The shell depends on Core; Core never depends on the shell.

---

## V1 Scope (Must-Have)

- Project list scanned from configurable root (qualifies via `CLAUDE.md`, `.claude/`, or manual entry)
- Tabs per active project session, optionally persistent across restarts
- Embedded terminal per tab (WebView2 + xterm.js + ConPTY)
- Activity indicator: cold / igniting / burning / embers / dead — driven by PTY-read timestamps
- Toolbar: Rekindle, Resume Last, Open in Explorer, Open External Shell
- Per-project quick-links (toolbar URL buttons, system browser via `Process.Start`; data shape sub-tab-friendly for V2)
- MCP server registry ("kindling"): global catalog + per-project activation + adapter-driven projection into the agent's expected format
- JSON config in `%APPDATA%\Firepit` (settings, projects, MCP registry, quick-links)
- Single-instance behavior

## Explicitly Out of Scope for V1

File browser, markdown viewer, image viewer, embedded PowerShell, project sub-tabs / cockpit layout, agent adapters beyond Claude Code, macOS/Linux, multiple themes, telemetry, crash reporting, any AI features beyond hosting agents, registry-editor UI for MCP, DPAPI in-file secrets, MCP marketplace discovery.

(Post-V1 note: an in-app update check + self-update landed after V1 — a caption-bar badge polls GitHub Releases and the Inno installer is run silently to update in place. See `Firepit.Core.Updates` and `MainWindow.Updates.cs`.)

If a request reads as V2/V3 territory, push back: confirm V1 is solid first. The sub-tab cockpit in particular is a magnet for scope creep — V1 ships the foundation (terminal-only tabs, quick-links open externally), V2 generalizes.

---

## Project Structure

```
firepit-ai/
├── src/
│   ├── Firepit/              # WPF main project (.exe entry)
│   ├── Firepit.Core/         # Domain models, interfaces (ITerminalView, IAgentAdapter)
│   ├── Firepit.Process/      # ConPTY, agent process management
│   ├── Firepit.Web/          # WebView2 hosting, xterm.js bridge, embedded resources
│   └── Firepit.Adapters/     # Per-agent adapters (V1: ClaudeCode only)
├── docs/
├── tests/
├── SPEC.md
├── CLAUDE.md
├── README.md
└── Firepit.sln
```

`Firepit.Web` owns everything WebView2/xterm.js. The xterm.js bundle ships as embedded resources, served to WebView2 via `SetVirtualHostNameToFolderMapping`. Do not load from `file://`.

---

## Key Behaviors

### Activity States (per tab)

| State | Trigger | Visual |
|---|---|---|
| `Cold` | Project added, no session | dim |
| `Igniting` | User clicked summon, agent launching | warming |
| `Burning` | PTY output within last `activityIdleThresholdMs` (default 500ms) | bright |
| `Embers` | No PTY output for >threshold | dimmed warm |
| `Dead` | Process exited | grey |

Driven by a 200ms-tick timer comparing now to `lastReadTimestamp`. Apply hysteresis on Burning↔Embers transitions to prevent flicker on slowly-streaming agents.

### Toolbar Actions

- **Rekindle**: kill child process, relaunch agent CLI (no `--continue`)
- **Resume Last**: relaunch with `--continue` (Claude Code) or adapter-equivalent
- **Open in Explorer**: `explorer.exe <project_path>`
- **Open External Shell**: `wt.exe -d <path>` if `wt` on PATH, else `powershell.exe -NoExit -Command "Set-Location <path>"` (config-overridable)

### Session Resumption

V1: just `--continue`. V2 introduces `--resume <session-id>` with a history dropdown.

---

## Code Conventions

- Target framework: .NET 10. AOT-friendly where reasonable; do not block on AOT for V1.
- Async by default. PTY I/O, FileSystemWatcher events, and process lifecycle are all async.
- Nullable reference types: enabled.
- DI: a lightweight container in the shell wires Core/Process/Adapters services into the WPF views.
- Logging: Serilog to `%LOCALAPPDATA%\Firepit\logs`, rotating file. Format and retention TBD (see SPEC §Open Questions).
- No code-side dependencies on brand vocabulary. If you write `class FireKindler`, you've drifted — rename.
- **The app is English. Every string.** Labels, tooltips, menu headers, toasts,
  dialogs, empty states, log messages, exception text, MCP tool descriptions —
  all English, no exceptions, no localization layer. The author works in German
  and sessions are usually held in German; that is the *conversation* language
  and it must not leak into the product. Before finishing any UI work, grep your
  diff for German (`ä ö ü ß`, `der|die|das|nicht|Datei|Ordner`) — this has
  regressed twice (v0.9.0 artifact pane, update badge tooltip).

---

## Testing

ConPTY-driven code is hard to test directly. Apply seams:

- `IPtyChannel` (read/write/close) abstracts the PTY so process-host logic is unit-testable with a fake.
- `IActivityClock` injects time into the activity detector — drive transitions deterministically.
- Adapters get golden-file tests for command-line construction.
- The WebView2 bridge is integration-tested end-to-end on a real WebView2 instance in a CI-skippable suite (CI on Windows runners only).

Avoid mocking what you don't own (ConPTY, WebView2). Wrap them in narrow interfaces and test against fakes.

---

## Distribution (V1)

GitHub Releases, single-file self-contained .NET publish. Two artifacts per `v*` tag: the published `bin/win-x64/` zipped, and an Inno Setup installer (`FirepitSetup-*.exe`, per-user `%LOCALAPPDATA%\Programs\Firepit`, no UAC). The portable zip still works drop-the-exe-anywhere. Installed builds self-update via the in-app update check (downloads the installer, runs it silently, relaunches); portable copies fall back to opening the release page.

The xterm.js bundle and `terminal.html` ship as embedded resources inside the exe — keep `dotnet publish` output to one file plus a runtime folder.

---

## Open Decisions (See SPEC §Open Questions)

License (MIT likely), single-instance mechanism (mutex vs. named pipe — pipe preferred for arg passing), session/scrollback persistence depth, logging retention, first-run UX.

When in doubt, defer: any of these can land after V1 prototype runs.

---

## Running the App During Development

Use **`./run.ps1`** (or the **`/run`** slash command) — never invoke `dotnet
build` + `Firepit.exe` manually. The script kills any running instance,
builds, and launches the canonical exe at `src/Firepit/bin/{Debug|Release}/Firepit.exe`.

`Directory.Build.props` sets `AppendTargetFrameworkToOutputPath=false`, but
old TFM-suffixed paths (`bin/Release/net10.0-windows10.0.17763.0/Firepit.exe`)
still exist on disk from before V1.12 and have caused "I'm running a stale
build" confusion. `run.ps1` always points at the right one. `./run.ps1 -Clean`
wipes those stale paths.

## Working with This Codebase

- Read `SPEC.md` first if you haven't. It is the contract.
- For any non-trivial change, write a short plan and confirm before refactoring across project boundaries.
- Prefer editing existing files over creating new ones, but do create the project skeleton if the solution is empty — `SPEC.md` defines the layout.
- Don't introduce a third-party library without a one-line justification in the PR/commit. Dependency creep is a real risk for a tool that markets itself as "no Electron, no bundle."
- The success criterion is personal: *"the author replaces three permanently-open PowerShell windows with one Firepit window and never goes back."* Optimize for that.
