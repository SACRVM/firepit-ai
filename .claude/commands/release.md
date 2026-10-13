---
description: Bump the Firepit version, update the changelog, tag, push, publish the GitHub release and reinstall the service on this machine.
argument-hint: [test|patch|minor|major|<x.y.z>]
---

# /release — cut a Firepit release

You are cutting a Firepit release. Follow these steps **exactly**, in order.
**Never skip the confirmation step.** Speak to SACRVM in German; commit
messages, tag names and release notes stay English.

A release is a `v*` tag on `main`, a GitHub release whose notes are the
changelog section, and the new build installed on this machine. CI only
builds and tests; nothing is uploaded.

The service and the bridge share one version: `<Version>` in
`src/Firepit.Daemon/Firepit.Daemon.csproj` and in
`tools/firepit-mcp/Firepit.Mcp.Bridge.csproj`. The commands mod
(`mods/firepit-commands`) has its own version in `plugin.json` and `VERSION`
in `hooks/register.tsx`; it changes when the mod changes, not here.

## Argument

`$ARGUMENTS` is one of:
- (empty) — work the bump out from the commits since the last tag
- `test` — dry run: show the plan, change nothing
- `patch` / `minor` / `major` — force that bump
- `X.Y.Z` — use this version

## Step 1 — Sanity checks

Stop and tell SACRVM if any of these fails.

```bash
git rev-parse --abbrev-ref HEAD                                    # main
test -z "$(git status --porcelain)" && echo clean || echo dirty    # clean
git fetch origin main --quiet
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" && echo synced || echo behind
gh auth status                                                     # logged in
```

## Step 2 — Current state

```bash
git describe --tags --match 'v*' --abbrev=0
grep -oP '(?<=<Version>)[^<]+' src/Firepit.Daemon/Firepit.Daemon.csproj
git log "$(git describe --tags --match 'v*' --abbrev=0)"..HEAD --format='%s'
```

## Step 3 — The bump

| Arg | Action |
|---|---|
| empty | any `feat` commit → **minor**; only `fix`/`build`/`chore` → **patch**; only `docs`/`refactor`/`test` → nothing to release, stop |
| `test` | as empty, then stop after Step 4 |
| `patch` / `minor` / `major` | force it (`major` before 1.0: confirm twice) |
| `X.Y.Z` | use it; must be greater than the current version |

Work from the csproj version, not the tag.

## Step 4 — Plan and confirmation

Show SACRVM, in German: last tag, current version, the commits grouped by
prefix, the proposed bump, the new version and tag, and the `[Unreleased]`
section of `CHANGELOG.md` that becomes the release notes.

`test`: stop here and say „Trockenlauf — nichts geändert."

Otherwise ask **„OK so? [j/n]"**. Only `j`, `ja`, `y` or `yes` continues.

## Step 5 — Version and changelog

- `<Version>OLD</Version>` → `<Version>NEW</Version>` in both csproj files.
  Touch no other field.
- In `CHANGELOG.md`, rename `## [Unreleased]` to `## [NEW] — YYYY-MM-DD`
  (today) and put a fresh, empty `## [Unreleased]` above it.

## Step 6 — Gate

```bash
dotnet build Firepit.slnx -c Release --nologo
dotnet test Firepit.slnx --nologo
```

On a failure: stop, show it, leave the tree as it is. No commit, no tag.

## Step 7 — Commit, tag, push, release

Commit with the repository's `Co-Authored-By` trailer, then:

```bash
git tag vNEW
git push origin main
git push origin vNEW
```

Write the new changelog section to a file in your scratchpad, then:

```bash
gh release create vNEW --title "vNEW" --notes-file <that file>
```

## Step 8 — Install here

```bash
scripts/install-linux.sh
journalctl --user -u firepit -n 5 --no-pager
```

The start line (`firepitd NEW starting`) must show the new version.

## Step 9 — Hand off

Tell SACRVM in German: the version, the release link, and that the service
on this machine runs it. Do not poll CI.

## Hard rules

- No tag push without the Step 4 confirmation.
- Never `--force`, never `--no-verify`, never move or delete a tag.
- The `/release` invocation is SACRVM's authorization for the tag push and
  the public GitHub release; nothing else is.
- Anything unclear (no commits since the last tag, the version and the tag
  disagree, a dirty tree): stop and ask instead of guessing.
