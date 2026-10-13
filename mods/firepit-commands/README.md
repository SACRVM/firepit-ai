# firepit-commands

The project commands from `.firepit/config.json`, inside Claude Code: a
button bar above the prompt, a panel with descriptions, logs and output, and
`/fire`.

```
Run: site ticker   Build: app broken   Git: status repo   …   panel
```

- **Bar** — each group once, then its buttons. `"bar": false` keeps a
  command in the panel only.
- **Panel** — the same groups, one line per command with its
  `description`; long-running commands show ● running with stop and logs;
  `details` shows what each command actually runs.
- **`/fire`** opens the panel, `/fire git status` runs one, `/fire trust`
  trusts the project's shell commands, `/fire status` says which version is
  loaded and what runs, `/fire reload` rereads the config.

How commands run on Linux: long-running ones as systemd user units
(`firepit-<project>-<id>`, logs in the journal), windowed ones in Konsole,
`inline` ones in the background with their output in the panel. Shell
commands run only after their exact command lines were trusted; a change to
any of them asks again. The patterns are in
`../../../firepit-template/.firepit/config.json`.

## Install

The repository is a plugin marketplace whose entry points at this folder, so
the installed mod is read from here — no copy to keep in sync.

```bash
claude plugin marketplace add ~/Projects/repos/firepit-ai
```

```bash
claude plugin install firepit-commands@firepit --scope user
```

After editing, `/reload-plugins` in a session picks the change up.

## Develop

```bash
claude plugin validate mods/firepit-commands
```

```bash
claude plugin test mods/firepit-commands
```
