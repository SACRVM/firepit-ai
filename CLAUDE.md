# CLAUDE.md

The operational brief for Claude Code sessions working on Firepit: what it
is, what not to build, and which lines must not be crossed.

## What Firepit is

The platform behind Claude Code for one person with many projects:

- **firepitd**, a systemd user service, owns projects, knowledge, inbox,
  commands, blueprints, integrity checks and scheduled jobs.
- **firepit-mcp** bridges every Claude Code session to it over MCP.
- **The `firepit` plugin** (a Claude Code mod plus skills) puts the
  project's commands and inbox into the Claude Code window.

Linux-first (CachyOS, KDE Plasma 6, Wayland). The old Windows shell (WPF,
WebView2, ConPTY) ended at v0.30.0 and lives on the `windows-legacy` branch;
`main` no longer carries it.

Read, in this order:

1. **`SPEC.md`**: the product, its scope and its non-goals. The *why*.
2. **`docs/ARCHITECTURE.md`**: processes, protocol, data, security. The
   *how*.
3. **`docs/LINUX.md`**: the decision record and the open milestones
   (M13–M16, gate ⛳2). The *what next*.

## What not to build

- **A window, terminal or shell.** Claude Code is the interface. A shell of
  its own comes back only through gate ⛳2 in `docs/LINUX.md`, and then as
  the milestones say, not as a side quest.
- **Model calls of Firepit's own.** Jobs start `claude -p`, and the mod
  hands messages to the open session. Nothing else asks a model.
- **Cloud, sync, accounts or teams.** One user, one machine, files on disk.
- **An MCP server catalog.** Claude Code owns which servers a session gets.
- **A file manager, an editor or a file shelf.** Showing files is Claude
  Code's job: sessions send them into the conversation or publish Claude
  artifacts. Firepit's own artifact list was retired for that reason.

If a request reads like one of these, say so before building it.

## Invariants

Breaking one is a regression even when the tests pass.

1. **Libraries know no host.** `Firepit.Core`, `Firepit.Knowledge`,
   `Firepit.Process`, `Firepit.Adapters` and `Firepit.Mcp` target plain
   `net10.0`. They must not reference the workspace, the daemon or any UI.
   Compare paths through `PlatformText` and build them through
   `FirepitPaths`, never with hard-coded separators or case rules.
2. **The workspace has no UI.** Whatever a shell would do goes through
   `ITabHost`; the daemon passes `HeadlessTabHost`.
3. **The bridge is a tunnel.** `tools/firepit-mcp` translates frames and
   references no Firepit assembly. The protocol lives in `McpHost`.
4. **One writer per file.** The workspace writes `.firepit/config.json` and
   the inbox moves. The mod reads files and writes only
   through Firepit's MCP tools. A rewrite keeps every field it does not
   understand the meaning of. `description` and `bar` were nearly lost that
   way.
5. **The mod writes state only when it changed.** Every write redraws the
   bar and the panel and retires the press handles of the old drawing; a
   timer that writes unchanged values makes clicks miss.
6. **Files you own.** Everything a project carries is plain JSON or markdown
   under its `.firepit/`. Derived data (indexes, run history) is never the
   only copy.
7. **Neutral code vocabulary.** `Project`, `InboxMessage`, `JobScheduler`.
   The fire belongs in user-facing copy only; `class FireKindler` is a
   regression.

## Build, test, run

```bash
dotnet build Firepit.slnx
dotnet test Firepit.slnx
```

The mod has no Node toolchain. Use the Claude Code CLI bundled with the
desktop app (`~/.config/Claude/claude-code/<version>/claude`):

```bash
claude plugin validate mods/firepit
claude plugin test mods/firepit
```

Run Firepit as it runs for real. `scripts/install-linux.sh` publishes the
daemon, reinstalls the bridge and restarts the service. Then:

```bash
systemctl --user status firepit
journalctl --user -u firepit -f
```

A throwaway daemon for experiments: `firepitd --instance dev --no-jobs`.
Point a session at it with `FIREPIT_INSTANCE=dev`.

Things that bit before:

- Desktop apps get the systemd user PATH: `~/.dotnet/tools` is on it,
  `~/.local/bin` is not. That is why the bridge is a .NET global tool.
- `pkill -f <pattern>` matches the Bash tool's own command line. Use
  `pkill -x firepitd`.
- The interactive shell is zsh/fish: quote globs (`'*.cs'`) and do not
  `echo =====`.
- The mod is read from the working copy. `/reload-plugins` picks up edits,
  but a session whose `/fire` registration came from an older copy needs a
  new session.

## Code conventions

- .NET 10, nullable enabled, warnings are errors, async by default.
- **The product is English. Every string.** That covers UI labels in the
  mod, tool descriptions, server instructions, log messages, exception
  text, docs and commit messages. The author works in German and sessions
  are usually held in German. That stays in the conversation. Before
  finishing, grep the diff for German (`ä ö ü ß`,
  `der|die|das|nicht|Datei|Ordner`). This has regressed three times.
- No new third-party dependency without a one-line justification in the
  commit message.
- Prefer editing an existing file to creating one. Match the comment
  density and the naming of the code around you.
- Agent-facing text (server instructions, tool descriptions, blueprint
  sections, the plugin's skills in `mods/firepit/skills/`) is product
  surface. It is read by every session, so keep it short, keep it true, and
  keep tool names spelled exactly (`FirepitServerInstructionsTests` and
  `PluginSkillsTests` check). A tool that changes behaviour changes the skill
  that teaches it in the same commit.

## Testing

- Seams instead of mocks of what Firepit does not own: `ITabHost`,
  `IJobRunner`, `IActivityClock`, injectable secret lookups, a fake
  `claude` script for process tests.
- The workspace is tested headless against a temporary projects root
  (`tests/Firepit.Workspace.Tests`).
- The mod's tests mount the bar and the panel on the `desktop` and
  `terminal` surfaces against a mocked engine and clock. Write the body
  once and loop it over both surfaces.
- Facts about Windows behaviour use `WindowsFact` and skip here.

## Releases

`/release` bumps `firepitd` and `firepit-mcp` together, closes the
changelog section, tags `v*`, publishes the GitHub release and reinstalls
the service. Milestones get `m*` tags. The mod versions itself in
`plugin.json` and `VERSION` in `hooks/register.tsx`; keep the two in step.

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

## Firepit inbox

At the start of a session, read any pending messages in `.firepit/inbox/*.md` — cross-project notes Firepit routes here. Act on each, then mark it done with the `firepit_inbox_complete` MCP tool, passing the message's filename as the `id`.

## Firepit knowledge

Before researching something that may already be known, query the knowledge base with the `firepit_knowledge_search` MCP tool (scope `both` covers this project plus the global base). Save durable findings with `firepit_knowledge_add` — written in English, per the indexing convention. The created markdown files live under `.firepit/knowledge/` and are committed like any other file.

## Firepit pinned knowledge

@.firepit/knowledge-pinned.md

The import above auto-loads the knowledge docs marked `pin: true` in their frontmatter — always-on rules that apply every session without a search. Firepit regenerates the file from the pinned docs; don't edit it directly. Pin/unpin via the pinned flag on `firepit_knowledge_add` / `firepit_knowledge_update`, and keep the pinned set small — everything else stays reachable through `firepit_knowledge_search`.

## Firepit conventions

<!-- claude-firepit-fragments -->

@../.firepit/projects/claude.md
@../.firepit/projects/claude-github-public.md

The two imports above are shared files in the Firepit central repo — edit them there and every project follows. They carry policy; the tools themselves are described by Firepit's MCP server at the handshake, so nothing is duplicated between the two.
