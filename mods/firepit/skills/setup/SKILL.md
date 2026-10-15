---
name: setup
description: "Set up a project for Firepit, or bring an existing one up to date: the blueprint, .mcp.json, the CLAUDE.md sections, .gitignore, where its knowledge lives, Linux commands, Windows leftovers. Use when the user asks to set up, onboard, migrate, modernise or clean up a project for Firepit, when a new repository should join, or when firepit_blueprint_check or firepit_integrity_check reports findings."
---

# Setting up a Firepit project

Firepit's project conventions are defined once, in the `firepit` blueprint of
the central project (`<projects root>/.firepit/blueprints/firepit/`), and shown
in use by the reference project `firepit-template`. Apply the blueprint rather
than recreating by hand what it does; then do the parts it cannot.

## 1. Look first

- `firepit_list_projects`: is the folder a known project? A folder under the
  projects root qualifies with a `CLAUDE.md` or a `.claude/` folder. For a new
  one, `firepit_create_project` registers it and applies the blueprint in the
  same call.
- `git status`: uncommitted work is the user's. Leave it out of your commits,
  and mention it.
- `firepit_blueprint_check` with `projectName` and `blueprint: "firepit"`: the
  actions an apply would take, and warnings.
- `firepit_integrity_check` with `projectName`: index, fragment imports,
  conformance. Findings marked `error` mean an agent is being misled now.
- **Public or private?** `gh repo view --json visibility`. It decides where the
  knowledge lives and which shared fragment `CLAUDE.md` imports. Also run
  `git remote -v`: a remote still pointing at an old account name rides a
  redirect.

## 2. Apply the blueprint

`firepit_blueprint_apply` with `projectName` and `blueprint: "firepit"`. It
creates what is missing and never touches what exists:

- the `CLAUDE.md` sections for the inbox, knowledge, pinned knowledge and
  the shared conventions, the last importing the fragments that match the
  repository's visibility. Sections are found by a marker, so text
  around them stays as it is;
- `.firepit/config.json`, from the commented scaffold, if there is none;
- the `.gitignore` block: `.firepit/inbox/`, `.firepit/runs/`,
  `.firepit/knowledge.db*`, `!.firepit/config.json`,
  `.claude/settings.local.json`, `.claude/*.lock`, `.claude/agent-memory/`;
- for a public repository, a pointer file at `.firepit/knowledge`, so the
  documents live in the private central store.

If the check warned about blanket ignore lines (`.firepit/` or `.claude/`
ignored as a whole, which hides shared config), ask the user, then apply again
with `fixBlanketIgnores: true`. That edits their `.gitignore`.

## 3. What the blueprint does not do

- **`.mcp.json`** in the project root must register the Firepit server under
  the bare command, which resolves through the PATH every Claude Code session
  gets. Merge it into an existing file; keep other servers.

  ```json
  { "mcpServers": { "firepit": { "type": "stdio", "command": "firepit-mcp" } } }
  ```

- **Imports and pointers use `/`.** `CLAUDE.md` imports read
  `@../.firepit/projects/claude.md`; a pointer file at `.firepit/knowledge`
  holds a path such as `../../.firepit/projects/<name>/knowledge`. Files
  written on Windows often carry `\`.
- **A public repository with a knowledge directory** (not a pointer) is the
  state in which research gets published. Stop and tell the user before
  saving or moving anything.
- **Commands** written for Windows (`powershell`, `pwsh`, `cmd`, `.exe`,
  drive letters, backslashes, `elevated`) need a Linux form or have to go. The
  `firepit:commands` skill has the patterns.
- **`.firepit/artifacts.json`** and a `CLAUDE.md` section *Firepit artifacts*
  are leftovers of a retired feature: Claude Code shows files itself now.
  Delete both (`git rm` where the file was committed).
- **Leftovers**: `bash.exe.stackdump` files are Git Bash crash dumps. Delete
  them, with `git rm` where they were committed.
- **Line endings**: a shell script with CRLF fails with
  `$'\r': command not found`. Convert to LF (`sed -i 's/\r$//' <file>`) and,
  where the repository has scripts, add `*.sh text eol=lf` to
  `.gitattributes`.
- **`.claude/settings.json`**: permissions or hooks naming Windows programs or
  paths no longer match anything. Tell the user which; the file is theirs.

## 4. Check and commit

- `firepit_blueprint_check` reports no pending actions;
  `firepit_integrity_check` reports no errors.
- The project's commands show in the Firepit panel. Shell commands wait for
  the user to trust them there.
- Commit the Firepit setup on its own (for example
  `chore: Firepit conventions`), apart from unrelated fixes. In a public
  repository the commit message is public too: describe the change, not the
  context. Push only as the project's `CLAUDE.md` allows.
