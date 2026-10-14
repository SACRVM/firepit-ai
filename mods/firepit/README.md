# firepit

Firepit inside Claude Code: the project commands from `.firepit/config.json`
as a button bar above the prompt, the project's inbox and pinned artifacts,
one panel for all three, `/fire`, and the skills that say how Firepit
projects are run.

```
Inbox 2   Run: site ticker   Build: app broken   Git: status repo   …   panel
```

- **Bar** — the inbox count when something is pending, then each command
  group once with its buttons. `"bar": false` keeps a command in the panel
  only.
- **Panel** (*Firepit*) — the inbox, the command groups with one line per
  command and its `description` (long-running ones show ● running with stop
  and logs; `details` shows what each command actually runs), and the
  artifacts.
- **Inbox** — each pending message in `.firepit/inbox/` with sender and age.
  A message opens in the viewer with *Hand to Claude* (sent as your request)
  and *Done* (`firepit_inbox_complete`). *new messages* decides what happens
  when one arrives while the session is open, per project: **tell** (default)
  — once the session is idle, Claude is told under the mod's name, summarises
  and waits for your go; **act** — Claude does the work, asks before anything
  irreversible, reports back and does not forward it; **off** — it only shows
  up here. What was already waiting when the session started is never handed
  over on its own; *all to Claude* does that on request.
- **Artifacts** — the links in `.firepit/artifacts.json`, newest first.
  Markdown and text open in the viewer, everything else with `xdg-open`;
  *folder* selects the file in the file manager; a link that points nowhere
  (such as a Windows path) says *missing*. *unpin* is
  `firepit_artifact_remove`; the file stays.
- **`/fire`** opens the panel, `/fire git status` runs a command,
  `/fire open vectorizer` switches to another project's session (or starts
  one; `firepit_open_project`),
  `/fire inbox` and `/fire artifacts` list them in the chat, `/fire trust`
  trusts the project's shell commands, `/fire status` says which version is
  loaded and what runs, `/fire reload` rereads everything.

*Done* and *unpin* need Firepit's MCP server (`firepit`, from the project's
`.mcp.json`) connected in the session; reading the inbox and the artifacts
does not.

How commands run on Linux: long-running ones as systemd user units
(`firepit-<project>-<id>`, logs in the panel and the journal), windowed ones
in Konsole, `inline` ones in the background — their output comes back as a
row in the chat, the last 60 lines with the exit code, and Claude gets it as
context the way it gets a `!` command's output. Shell
commands run only after their exact command lines were trusted; a change to
any of them asks again. The patterns are in
`../../../firepit-template/.firepit/config.json`.

## Skills

Procedures an agent loads when the work calls for them, so every project is
run the same way. They name only tools that exist; a test in
`tests/Firepit.Core.Tests/Mcp/PluginSkillsTests.cs` checks.

| Skill | For |
|---|---|
| `firepit:setup` | Setting a project up for Firepit or bringing an old one up to date: blueprint, `.mcp.json`, `CLAUDE.md`, `.gitignore`, knowledge placement, Windows leftovers |
| `firepit:commands` | Writing project commands: groups and names, descriptions, inline output, dev servers, confirmations, trust, the Linux patterns |
| `firepit:messages` | Messages between projects: when to send, subject and body, release notices, handling and completing, never forwarding |
| `firepit:knowledge` | The knowledge base: search first, what to save, project or global, pinning, correcting, public repositories |

Firepit's server instructions point every session at them.

## Install

The repository is a plugin marketplace whose entry points at this folder, so
the installed mod is read from here — no copy to keep in sync.

```bash
claude plugin marketplace add ~/Projects/repos/firepit-ai
```

```bash
claude plugin install firepit@firepit --scope user
```

Until 0.6.0 the plugin was called `firepit-commands`; uninstall that one
(`claude plugin uninstall firepit-commands@firepit`). Its store does not carry
over, so each project's shell commands ask for trust once more and the inbox
mode starts at *tell*.

After editing, `/reload-plugins` in a session picks the change up.

## Develop

```bash
claude plugin validate mods/firepit
```

```bash
claude plugin test mods/firepit
```
