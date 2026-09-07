# Fixture projects

Three fake projects for looking at Firepit's own interface. Point a named
instance's project root here and it discovers them like any others — without
touching a real repository, and without a second agent ending up in a working
tree someone is using.

They exist because Firepit's UI could not be checked before shipping. The unit
tests answer *what* the artifact viewer puts in a document; they cannot answer
whether the close button fits its caption row, whether a code block's scrollbar
frames the block, or whether a maximised window leaves a dead strip down one
side. All three of those were wrong at once, and all three were found by
opening the window.

## Using them

```powershell
./run.ps1                     # starts the 'dev' instance
```

Then set that instance's project root to this folder — Settings → project root,
or directly in `%APPDATA%\Firepit-dev\settings.json`:

```json
"projectsRoot": "D:\\repos\\firepit-ai\\tests\\fixtures\\projects"
```

Only the named instance changes. The installed Firepit keeps its own settings
and never sees this.

## What each one is for

| Project | Qualifies through | Shows |
|---|---|---|
| `sample-report` | `CLAUDE.md` | The viewer with real content: a markdown report with a table, a code block wide enough to scroll, a quote, a task list and a relative image; a long text log; the image itself |
| `sample-empty` | `.claude/` | The artifact pane's empty state, which otherwise appears once in a project's life. Also covers the second discovery marker |
| `sample-broken` | `CLAUDE.md` | The failures: an artifact whose file was deleted, so the dead link renders dimmed rather than looking fine until clicked, and an archive the viewer refuses so the click falls through to the system handler |

## Rules

Opening one of these as a tab starts **PowerShell, not an agent** — each
`.firepit/config.json` overrides the agent command. Looking at Firepit's
interface should not cost a Claude session, and a fixture is not a place where
one should be working.

`tests/fixtures/**` is exempted from `.gitignore` at the bottom of that file.
These are fake projects that must be checked out whole: a `.log` or a
`.firepit/` file in here is content, not build output. Two ignore rules —
`logs/` and `*.log` — had already silently swallowed the text fixture before
that exemption existed.

Nothing in these directories is an instruction to anybody. The `CLAUDE.md`
files say so themselves, because a fixture project is exactly the sort of thing
an agent could otherwise read as a brief.
