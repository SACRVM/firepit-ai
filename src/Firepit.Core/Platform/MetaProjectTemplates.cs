namespace Firepit.Core.Platform;

/// <summary>
/// Template content for the .firepit meta-project. Inline strings (rather
/// than embedded resources) for v0.5.0 simplicity — moves to embedded
/// resources if templates need to grow much larger.
/// </summary>
internal static class MetaProjectTemplates
{
    public const string ClaudeMd = """
# Firepit central project

This is your meta-workspace — a hub for cross-project work that sits one level up from any single codebase.

You're Claude Code running inside it. Firepit's MCP server is registered for this session, which means you can adjust Firepit itself by talking to me about your projects.

## What you can do here

**Talk to Firepit through MCP tools** (all prefixed `firepit_`):

- `firepit_list_projects()` — what's around
- `firepit_open_tab(projectName, resume?)` — open or focus a tab
- `firepit_focus_tab(projectName)` — bring an existing tab forward
- `firepit_close_tab(projectName)` — close it (kills the agent)
- `firepit_reload(projectName, restart?)` — re-read `<project>/.firepit/config.json` and apply
- `firepit_send_to(toProject, subject, body, priority?)` — drop a markdown note in another project's `.firepit/inbox/`
- `firepit_inbox_list(projectName?)` — list pending messages in a project's inbox (defaults to your own)
- `firepit_inbox_complete(id, projectName?)` — mark a message as processed (moves it to `inbox/processed/`)

Resources you can read:

- `firepit://projects` — discovered + manual project list with open/closed status
- `firepit://sessions` — open tabs with their activity state (Burning / Embers / Dead)
- `firepit://settings` — current global settings, secrets redacted

## How Firepit configuration works (v0.5.0+)

Per-project Firepit config lives in `<project>/.firepit/config.json`. The file is yours to read, write, and commit (or gitignore). Sections:

- `quickLinks[]` — toolbar URL buttons. Hot-reloadable.
- `mcpActivations[]` — which MCP servers are active for this project, with optional `argOverrides`, `envOverrides`, `headerOverrides`. Restart needed.
- `agent.{command, args, envOverrides}` — per-project agent override. Restart needed.
- `session.envOverrides` — extra env vars on the PTY. Restart needed.

When you edit `<project>/.firepit/config.json`, follow up with `firepit_reload(projectName)` so the live tab picks up the change. Pass `restart: true` if you changed an MCP, agent, or env field — Firepit will surface the same as a "restart needed" banner anyway, but explicit is faster.

## Cross-project messaging (Phase 5)

When you want to ping another project's Claude with a feature request, bug report, or note, use `firepit_send_to`. The receiving project's Claude reads `.firepit/inbox/*.md` at session start.

The `from` field is auto-set from your `FIREPIT_PROJECT_NAME` env var — you don't need to declare yourself.

## Recommended setup: Fishbowl per project

If you use Fishbowl as your personal memory store, the canonical pattern is **one Fishbowl team per Firepit project**, each with its own bearer token:

```bash
cd /path/to/the-fishbowl
dotnet run --project tools/init-project -- <project-slug>
```

The tool prints a `cmdkey` line and an `mcpOverrides` snippet. Apply both, then `firepit_reload(projectName, restart: true)` and Fishbowl's MCP shows up in that project's session, scoped to that team's data.

See [`docs/FISHBOWL.md`](https://github.com/SACRVM/firepit-ai/blob/main/docs/FISHBOWL.md) in the firepit-ai repo for the full integration writeup.

## Folders

- `notes/` — free-form cross-project notes you'll want to come back to
- `.firepit/inbox/` — incoming messages from other projects' Claudes
- `.firepit/config.json` — this project's own Firepit config
- `.claude/settings.json` — Claude Code config (firepit MCP preregistered)

## What's NOT in scope

- Don't reach into other projects' source code from here. Each project's tab is the right place for that.
- Don't accumulate copies of code; this is for cross-cutting knowledge and orchestration.

If something doesn't fit anywhere, it probably doesn't fit here either — ask me.
""";

    public const string ReadmeMd = """
# .firepit central

Hub workspace for cross-project work. Open this in Firepit and Claude has the `firepit_*` MCP tools available — you can ask Claude to manage your other projects from here.

See `CLAUDE.md` for the full surface.

## Inbox

When another project's Claude pings you (`firepit_send_to`), the message lands in `.firepit/inbox/`. From v0.5.15 the Firepit toolbar has an **Inbox** button per tab — click it to hand the whole queue to Claude (or just type "verarbeite Inbox" and Claude picks them up via `firepit_inbox_list` / `firepit_inbox_complete`). Processed messages move to `.firepit/inbox/processed/`.

## Notes

`notes/` is unstructured. Drop markdown about cross-project decisions, naming conventions, recurring patterns, anything you'll want to come back to.
""";

    public const string ClaudeSettingsJson = """
{
  "mcpServers": {
    "firepit": {
      "command": "firepit-mcp",
      "args": []
    }
  }
}
""";

    public const string FirepitConfigJson = """
{
  "version": 1,
  "id": "firepit-central",
  "mcpActivations": [
    { "id": "firepit" }
  ]
}
""";

    public const string NotesReadmeMd = """
# Notes

Cross-project markdown drops. No imposed structure.

Suggested patterns:

- `naming-conventions.md` — how you name things across repos
- `decisions/<date>-<topic>.md` — date-stamped decisions
- `tools/<name>.md` — notes about tools you use across projects
""";

    public const string GitIgnore = """
# Inbox is local — messages from your own machine to your own machine.
.firepit/inbox/
.firepit/inbox/processed/

# Local agent state
.claude/projects/
.claude/sessions/

# Local Firepit-specific overrides — keep your own machine's bearer
# tokens / paths out of git. Strip this line if you want to share.
.firepit/config.local.json
""";
}
