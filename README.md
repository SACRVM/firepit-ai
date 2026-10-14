# Firepit

> *Where your agents gather.*

Firepit is the platform behind Claude Code for people who keep many projects
on one machine. It is a small user service that every Claude Code session can
reach, in the desktop app or the CLI and in any project. Through it, sessions
share one knowledge base, send each other messages from project to project, pin
the files they produce, run scheduled jobs, and keep each repository's Claude
setup in step with a blueprint. A Claude Code mod adds each project's commands,
inbox and artifacts to the Claude Code window.

Everything lives in plain files in your repositories and under `$HOME`.
There is no cloud service, no account and no window of its own: Claude Code is
the interface.

> **Status:** Linux-first. Built and used on CachyOS with KDE Plasma 6 on
> Wayland. Firepit started as a Windows terminal shell. That shell ended at
> v0.30.0 and lives on the [`windows-legacy`](https://github.com/SACRVM/firepit-ai/tree/windows-legacy)
> branch; what it had grown underneath is what Firepit is now. See
> [History](#history).

## How it fits together

```
Claude Code (desktop app, CLI): one session per project
 ├─ firepit-commands mod ─ bar · panel · /fire: commands, inbox, artifacts
 └─ MCP server "firepit" ─ firepit-mcp, a stdio bridge
                             │  Unix socket, $XDG_RUNTIME_DIR/firepit/mcp.sock
                             ▼
                    firepitd, a systemd user service
                    projects · knowledge index · inbox · artifacts
                    scheduled jobs · blueprints · integrity checks
                             │
                             ▼
       ~/Projects/repos/<project>/.firepit/   files you own and commit
       ~/Projects/repos/.firepit/             the central project
```

## What it does

- **Knowledge base.** Markdown documents in each project's
  `.firepit/knowledge/` and in a global base, found by meaning, not only by
  words. Search combines SQLite full-text search with local MiniLM vectors
  (ONNX, sqlite-vec), and no text leaves the machine. Documents marked
  `pin: true` are loaded into every session of their project through
  `CLAUDE.md`. Tools: `firepit_knowledge_search`, `_get`, `_add`, `_update`
  and `_delete`.
- **Inbox between projects.** `firepit_send_to` drops a message into another
  project's `.firepit/inbox/`. The message waits there until a session in
  that project handles it and marks it done with `firepit_inbox_complete`.
  The mod shows pending messages and, if you want, hands new ones to an idle
  session.
- **Artifacts.** Reports, screenshots, logs and builds that a session wants
  you to open get pinned with `firepit_artifact_add`. They are listed in the
  mod's panel, and markdown and text open right there.
- **Project commands.** Each project's `.firepit/config.json` defines buttons
  such as `Run: site` or `Git: status`. Long-running commands run as systemd
  units, the others in Konsole or with their output in the chat. A shell
  command only runs once you have trusted it.
- **Scheduled jobs.** Cron-scheduled `claude -p` runs per project, with tool,
  turn and budget limits, and their history.
- **Blueprints and shared conventions.** One blueprint describes what a
  Firepit project carries: the `CLAUDE.md` sections, the `.mcp.json` entry
  and the ignore rules. Shared policy lives in the central project and is
  imported into each `CLAUDE.md`, so editing it once changes every project.
  Tools: `firepit_blueprint_list`, `_check` and `_apply`.
- **Integrity checks.** `firepit_integrity_check` compares what Firepit
  derives with what is on disk: the knowledge index against the documents,
  the `CLAUDE.md` imports, blueprint conformance. With `repair` it rebuilds
  what it derived. It never edits a document of yours.
- **One click to a project.** The start menu gets a *Projects* submenu with
  every project, and KRunner finds them too (Alt+Space, `vec`, Enter). An
  entry opens the project's session in the Claude desktop app, the most
  recent one in its folder, or starts one there. `/fire open vectorizer` and
  `firepit_open_project` do the same from inside a session.
- **Projects.** Every folder under the projects root that has a `CLAUDE.md`
  or a `.claude/` folder is a project. Each session finds its project from
  the folder it runs in. You can also create and rename projects
  (`firepit_create_project`, `firepit_rename_project`); a rename moves the
  folder and Claude Code's history with it.

## Install

You need the .NET 10 SDK, a systemd user session and Claude Code. The
commands mod opens Konsole and Dolphin by default.

```bash
git clone https://github.com/SACRVM/firepit-ai.git ~/Projects/repos/firepit-ai
~/Projects/repos/firepit-ai/scripts/install-linux.sh
```

The script installs or updates everything under `$HOME`:

- `firepitd` to `~/.local/opt/firepit`;
- `firepit-mcp` as a .NET global tool in `~/.dotnet/tools`, which is on the
  PATH that desktop apps get (`~/.local/bin` is not);
- `firepit.service` as an enabled systemd user unit, which it then
  (re)starts.

Run it again after a `git pull` to update.

**Connect a project.** Put this `.mcp.json` in the project root. Applying a
blueprint does it for you, together with the `CLAUDE.md` sections:

```json
{ "mcpServers": { "firepit": { "type": "stdio", "command": "firepit-mcp" } } }
```

**Install the mod.** The repository is its own plugin marketplace. The
installed mod is read from the working copy, so a `git pull` followed by
`/reload-plugins` updates it.

```bash
claude plugin marketplace add ~/Projects/repos/firepit-ai
claude plugin install firepit-commands@firepit --scope user
```

On first use, Firepit downloads the embedding model (all-MiniLM-L6-v2, about
90 MB) from Hugging Face. Scheduled jobs need the `claude` CLI on the
service's PATH. The copy bundled inside the desktop app does not count.

## A project's `.firepit/`

| Path | What it is | Commit it? |
|---|---|---|
| `config.json` | Commands, scheduled jobs, project id (JSONC) | yes |
| `knowledge/*.md` | The project's knowledge documents | yes |
| `knowledge-pinned.md` | Pinned documents, regenerated by Firepit and imported by `CLAUDE.md` | yes |
| `artifacts.json` | Pinned files, as paths relative to the project | your call |
| `inbox/` | Pending messages; handled ones move to `inbox/processed/` | no |
| `runs/` | History of the scheduled job runs (30 days) | no |
| `knowledge.db*` | The derived search index | no |

Commands look like this. The panel shows the `description`, and an `inline`
command answers in the chat:

```jsonc
{
  "version": 1,
  "id": "my-project",
  "commands": [
    { "group": "Run", "name": "site", "description": "Serves the site on :8000.",
      "type": "shell", "command": "python3", "args": ["-m", "http.server"],
      "window": "reuse:site", "longRunning": true },
    { "group": "Git", "name": "status", "type": "shell",
      "command": "git", "args": ["status", "-sb"], "window": "inline" },
    { "group": "Git", "name": "repo", "type": "url",
      "url": "https://github.com/me/my-project" }
  ]
}
```

The [mod's README](mods/firepit-commands/README.md) covers the bar, the
panel, the inbox modes and `/fire`.

## Where things live

| What | Where |
|---|---|
| Settings | `~/.config/firepit/settings.json`, optional; the projects root defaults to `~/Projects/repos` |
| Logs, state, embedding model | `~/.local/share/firepit/` |
| The MCP socket | `$XDG_RUNTIME_DIR/firepit/mcp.sock` (mode 0600) |
| Global knowledge, shared `CLAUDE.md` fragments, blueprints | `<projects root>/.firepit/`, the central project |
| The service | `systemctl --user status firepit`, `journalctl --user -u firepit -f` |
| Start menu entries | `~/.local/share/applications/firepit/`, the *Projects* submenu in `~/.config/menus/applications-merged/firepit-projects.menu` |

Secrets in settings are referenced as `${env:NAME}` or `${cred:<target>}`
and never stored as plain values. A `cred:` reference is looked up in the
Secret Service (KWallet, GNOME Keyring) through `secret-tool`, under
`service=firepit target=<target>`.

## Develop

```bash
dotnet build Firepit.slnx
dotnet test Firepit.slnx
claude plugin test mods/firepit-commands
```

| Project | Role |
|---|---|
| `src/Firepit.Core` | Settings, projects, project config, inbox, artifacts, blueprints, jobs, secrets |
| `src/Firepit.Knowledge` | Knowledge store, indexer, hybrid search, embeddings |
| `src/Firepit.Mcp` | The MCP host: tools, resources, the socket server |
| `src/Firepit.Workspace` | Everything the tools act on, without a window |
| `src/Firepit.Daemon` | `firepitd`: the workspace and the MCP host as a service |
| `src/Firepit.Process`, `src/Firepit.Adapters` | Processes for jobs, the Claude Code command line |
| `tools/firepit-mcp` | The stdio ↔ socket bridge Claude Code starts |
| `mods/firepit-commands` | The Claude Code mod |

[`SPEC.md`](SPEC.md) says what Firepit is for and what it is not.
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) is the technical contract.
[`docs/LINUX.md`](docs/LINUX.md) records the move to Linux and the open
milestones. [`CHANGELOG.md`](CHANGELOG.md) has the release notes, and
[`CLAUDE.md`](CLAUDE.md) is the brief for Claude sessions that work on this
repository.

## History

From May to October 2026, Firepit was a native Windows shell: WPF, xterm.js
in WebView2 and ConPTY. It hosted Claude Code sessions in tabs, with activity
indicators (*burning*, *embers*), a project switcher and a toolbar per
project. Over time it grew the parts this repository is now made of: the MCP
server, the knowledge base, the inbox, artifacts, scheduled jobs and
blueprints.

When its author moved to Linux, Claude Code's desktop app already did the
hosting well. The shell stayed behind, and the platform came along. The last
Windows release is v0.30.0, and its code is on the
[`windows-legacy`](https://github.com/SACRVM/firepit-ai/tree/windows-legacy) branch.

## License

MIT. See [`LICENSE`](LICENSE).

---

*The fire is lit. Pull up a session.*
