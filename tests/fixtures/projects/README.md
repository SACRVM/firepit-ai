# Fixture projects

Three fake projects for looking at what Firepit shows: the artifact list and
viewer in the mod's panel, an empty project, dead links. A named Firepit
instance whose projects root points here discovers them like any others,
without touching a real repository.

They exist because an interface cannot be checked by unit tests alone. The
tests answer *what* the viewer gets; they cannot answer whether a long code
block scrolls, whether a dead link reads as dead before it is clicked, or
what an empty list looks like.

## Using them

Give a named instance its own settings, with this folder as the projects
root:

```bash
mkdir -p ~/.config/firepit-dev
echo '{ "projectsRoot": "'"$PWD"'/tests/fixtures/projects" }' > ~/.config/firepit-dev/settings.json
```

Start that instance next to the real one, without jobs:

```bash
dotnet run --project src/Firepit.Daemon -- --instance dev --no-jobs
```

Then open a Claude Code session in a fixture folder with
`FIREPIT_INSTANCE=dev` in its environment, so its bridge dials the dev
instance. The commands mod reads the fixture's files directly, so its panel
shows them even without the instance. Only the dev instance changes; the
installed Firepit keeps its own settings and never sees this folder.

## What each one is for

| Project | Qualifies through | Shows |
|---|---|---|
| `sample-report` | `CLAUDE.md` | The viewer with real content: a markdown report with a table, a code block wide enough to scroll, a quote, a task list and a relative image; a long text log; the image itself |
| `sample-empty` | `.claude/` | The empty artifact list, which otherwise appears once in a project's life. Also covers the second discovery marker |
| `sample-broken` | `CLAUDE.md` | The failures: an artifact whose file was deleted, so the link shows as missing rather than looking fine until clicked, and an archive the viewer does not open, so the click falls through to the system handler |

## Rules

A session opened in a fixture is for looking at the panel, not for working:
there is nothing here to build, and the `CLAUDE.md` files say so.

`tests/fixtures/**` is exempted from `.gitignore` at the bottom of that file.
These are fake projects that must be checked out whole: a `.log` or a
`.firepit/` file in here is content, not build output. Two ignore rules —
`logs/` and `*.log` — had already silently swallowed the text fixture before
that exemption existed.

Nothing in these directories is an instruction to anybody. The `CLAUDE.md`
files say so themselves, because a fixture project is exactly the sort of thing
an agent could otherwise read as a brief.
