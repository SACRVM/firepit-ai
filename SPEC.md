# Firepit

> *Where your agents gather.*

The product contract: what Firepit is for, what it refuses to be, and the
rules every change keeps. The technical contract is
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); the record of how it got here
is [`docs/LINUX.md`](docs/LINUX.md).

## TL;DR

Firepit is a local platform for one person who runs Claude Code across many
projects. It has three parts:

- **firepitd**, a user service, owns the projects, the knowledge base, the
  inbox, scheduled jobs and blueprints.
- **firepit-mcp**, a bridge, gives every Claude Code session those as MCP
  tools.
- **firepit**, a Claude Code plugin, puts the project's commands, inbox
  into the Claude Code window, with skills for how projects are run.

Claude Code is the interface. Firepit has no window of its own.

## The problem

Fifty repositories, each with its own Claude sessions, and each session is an
island:

- What one session worked out, the next one in another project researches
  again.
- A library ships a release; nine projects that vendor it should hear about
  it, and none of them is open.
- A session produces a report or a build, and its path scrolls away.
- Every project needs its handful of commands: serve the site, run the
  tests, open the repo. That means terminals to keep open.
- The `CLAUDE.md` conventions drift apart across fifty repositories.

Claude Code solves the session: terminal, diffs, permissions, history. It
does not solve the *between*: between sessions, between projects, between
today and next month. Firepit is the between.

## What Firepit is

- **A memory across projects.** A knowledge base of markdown documents per
  project plus a global one, searched by meaning, written by the sessions
  themselves. A few pinned rules load into every session of their project.
- **A post office.** Projects send each other messages that wait in the
  recipient's inbox until a session handles them. A session that is open and
  idle can be told the moment one arrives.
- **A door.** Every project sits in the start menu and in KRunner; one
  click opens its Claude session.
- **A switchboard.** Each project's commands are buttons and `/fire` phrases,
  run the Linux way: systemd units, Konsole, or output in the chat.
- **A clock.** Scheduled `claude -p` jobs per project.
- **A standard.** Blueprints and shared `CLAUDE.md` fragments keep fifty
  repositories wired the same way, and an integrity check says where they
  are not.

## What Firepit is not

- **Not a terminal, shell or window.** The desktop app and the CLI host
  sessions better than Firepit's own shell did; that shell is retired (see
  History). A new one gets built only if decision gate ⛳2 in
  `docs/LINUX.md` says it is missed.
- **Not an agent.** Firepit never talks to a model itself. Scheduled jobs
  start `claude -p`, and the mod hands messages to the session that is
  already open; nothing else asks a model anything.
- **Not a cloud service, a team tool or an orchestrator.** One user, one
  machine, files on disk. Nothing is synced except what git syncs.
- **Not an IDE, a file manager or a file shelf.** Showing a file is Claude
  Code's job (a file sent into the conversation, a Claude artifact); Firepit
  keeps no list of them. Files stay where they are and open in their own
  programs.
- **Not the MCP configuration.** Claude Code owns which servers a session
  gets (`.mcp.json`, user settings). Firepit is one of those servers. The old
  shell's registry projected a server catalog into the sessions it launched;
  with no launcher, that is gone.

## Principles

1. **Files you own.** Everything a project carries — config, knowledge,
   inbox — is plain JSON or markdown in its `.firepit/`
   folder, readable without Firepit and committed or ignored as the
   repository decides. Derived data (search indexes, run history) is never
   the only copy of anything.
2. **Claude Code is the interface.** Firepit reaches the user through MCP
   tools and the mod, never through a window of its own. What the user
   sees, they see where they already work.
3. **One writer per file.** The service writes through its tools; the mod
   reads files and writes only through those tools. Two writers to one JSON
   file is a bug even if it works.
4. **The user is the gate.** A shell command runs only after its exact
   command line was trusted. A delivered message asks Claude to summarise
   and wait unless the project was set to act. Nothing irreversible happens
   on a message's say-so.
5. **Local and quiet.** No network listener (a Unix socket, mode 0600), no
   telemetry. The only download is the embedding model, once.
6. **English product.** Every string a user or an agent reads — UI,
   messages, logs, tool descriptions — is English. The author works in
   German; that stays in the conversation.

## Scope

| Area | What is in it |
|---|---|
| Projects | Discovery under one root (`CLAUDE.md` or `.claude/` qualifies), manual entries, the central project (`<root>/.firepit`), create, rename with history migration |
| Knowledge | Project and global scopes; add, update, delete, get, hybrid search (full-text + local vectors, full-text alone when vectors are unavailable); pinning; pointer files that keep a public repository's notes in the central project |
| Inbox | Send to a project, list, complete; the mod's badge, viewer and delivery modes (off, tell, act) |
| Commands | `.firepit/config.json` commands: shell (new window, inline, reusable or long-running), Claude prompt, URL; groups, descriptions, bar flag, confirm, trust |
| Jobs | Cron schedules per project, timezone, concurrency policy, tool, turn and budget limits, run history |
| Blueprints | List, check, apply; shared `CLAUDE.md` fragments chosen by repository visibility |
| Integrity | Index against documents, fragment imports, blueprint conformance; repair what is derived |

**Out of scope:** a terminal host (unless ⛳2 says otherwise), agents other
than Claude Code, more than one user, sync, Windows and macOS releases (the
libraries stay portable; only Linux is tested and released), an MCP server
catalog, any model call of Firepit's own.

## Brand language

The fire belongs to the name, the tagline and the closing lines of the
README. Words like *gather*, *kindle* and *embers* can stay there. The text
people work with is plain: "Inbox 3", "Hand to Claude", "Firepit is not
running". Code, logs and tool descriptions use neutral names (`Project`,
`InboxMessage`, `JobScheduler`); a class called `FireKindler` is a regression.

## Success criterion

Personal, as it always was. The author opens Claude Code in any of their
projects, and what the others know, ask and produce is one tool call or one
click away. They do not miss the old shell; ⛳2 is how that gets checked.

## Open questions

- **⛳2:** after two weeks of daily use, is a shell of its own still missed?
  If not, M13–M14 in `docs/LINUX.md` stay unbuilt.
- **Packaging:** the install script is enough for one machine; a `PKGBUILD`
  is the Arch way if a second machine appears.
- **Jobs and the CLI:** scheduled jobs need the `claude` CLI on the
  service's PATH; the copy bundled with the desktop app is not on it.
- **Delivery gate:** the old shell knew which tab the user was looking at
  and only ever asked that one to wait. Claude Code does not tell a mod
  that, so the gate is a mode per project for now.

## History

Firepit V1 (May 2026) was a native Windows shell that hosted agent CLIs in
tabs: WPF, xterm.js in WebView2, ConPTY, with activity states *cold*,
*igniting*, *burning*, *embers* and *dead*. The v0.5 line added the platform
layer: per-project `.firepit/`, the MCP server, the central project, the
inbox. Knowledge, artifacts, jobs and blueprints followed through v0.30.0.
In October 2026 the author moved to Linux and the platform moved with them,
without the shell. The V1 spec, roadmap and design notes are on the
`windows-legacy` branch.

## License

MIT.
