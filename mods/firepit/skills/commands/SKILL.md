---
name: commands
description: "Write or change a project's commands in .firepit/config.json: the buttons above the prompt, the rows of the Firepit panel and /fire. Covers naming and groups, descriptions, output in the chat, dev servers, confirmations and the Linux patterns. Use when the user wants a button, shortcut or command for building, running, testing, deploying or opening something, or asks to add, fix, rename, migrate or clean up project commands."
---

# Project commands

A project's commands live in `.firepit/config.json` under `commands`. The
Firepit plugin shows them as buttons above the prompt and as rows in its
panel, and `/fire <group> <name>` runs one from the prompt. The file is JSONC
and the plugin rereads it on every save.

## Naming

- Every command sits in a **group** named after its area: `Run`, `Build`,
  `Test`, `Git`, `Open`, `Tools`, `Claude`. Its **name** is short, lowercase
  and unique within the group. Group and name read as a phrase: *Run site*,
  *Git status*, *Open repo*.
- The bar shows each group once, then its buttons (`Git:` status repo). Keep
  the bar short: `"bar": false` keeps rare commands in the panel only.
- `description` is one sentence for the panel saying **what** the command
  does, not how ("Serves the site on :8765."). The command line shows under
  *details* and in the trust review anyway.

## Patterns (Linux)

| Need | Pattern |
|---|---|
| a plain program | `"command": "git", "args": ["status", "-sb"]`: an argv, no shell |
| pipes, `&&`, redirects | a script in `scripts/`, run as `"command": "bash", "args": ["scripts/x.sh"]` |
| output in the chat, where Claude can read it | `"window": "inline"` (tests, status, linters) |
| a terminal window | `"window": "new"`, the default: Konsole |
| a dev server or watcher | `"window": "reuse:<id>", "longRunning": true`: a systemd user unit with stop and logs in the panel |
| one window at a time | `"window": "reuse:<id>"` |
| read the error of a failed build | `"keepOpenOnError": true` |
| something destructive | `"confirm": true`: the bar asks first |
| a subfolder, extra variables | `"cwd": "sub/dir"`, `"env": { "KEY": "value" }`; `null` unsets |
| ask Claude something recurring | `"type": "claude-prompt", "prompt": "…"`: sent as the user's prompt |
| open a page | `"type": "url", "url": "https://…"` |

Never: `powershell`, `pwsh`, `cmd`, `.exe`, drive letters, backslashes,
`elevated`. Secrets do not go into `env`; a command reads them from the
environment or the secret store at run time.

```jsonc
{
  "group": "Run", "name": "site",
  "description": "Serves site/ on 127.0.0.1:8765.",
  "type": "shell",
  "command": "python3",
  "args": ["-m", "http.server", "8765", "--bind", "127.0.0.1", "--directory", "site"],
  "window": "reuse:site",
  "longRunning": true
}
```

## Changing them

- `firepit_add_command` adds a command, or replaces the one with the same
  group and name in place; `firepit_remove_command` takes the phrase
  (`"Git status"`), or the bare name when only one group has it.
  `firepit_list_commands` shows what is there. The tools keep every field but
  rewrite the file without its comments.
- Editing the file by hand keeps the comments; keep it valid JSONC.
- Old `quickLinks` still show as a *Links* group. Move them into `url`
  commands in the group they belong to when you touch the file.

## Trust

A shell command runs only after the user has trusted the project's shell
commands in the panel (or with `/fire trust`). Any change to what a shell
command runs or how (its name, command line, working directory, environment,
window or `longRunning`) asks again.
Say so when you add or change one: the user reviews it before anything runs.
Never trust on the user's behalf.

## Trying one

`/fire <group> <name>` runs it the way the button does. An inline command's
output comes back as a row in the chat. `/fire status` shows the loaded
config, the trust state and what is running. A long-running command's logs
are in the panel and in `journalctl --user -u firepit-<project>-<id>`.
