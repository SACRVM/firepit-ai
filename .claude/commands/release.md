---
description: Bump Firepit version, tag, push — ci.yml then publishes the single-file Firepit.exe and the Inno installer to the GitHub Release.
argument-hint: [test|patch|minor|major|<x.y.z>]
---

# /release — cut a Firepit release

You are cutting a Firepit release. Follow these steps **exactly**, in order.
**Never skip the confirmation step.** Speak to SACRVM in German; commit
messages and tag names stay English.

The release pipeline produces two artifacts from a single `v*` tag push:

1. `firepit-vX.Y.Z-win-x64.zip` — the published `bin/win-x64/` folder zipped.
2. `FirepitSetup-X.Y.Z-win-x64.exe` — Inno Setup installer (built in CI by ISCC).

Both attach to the GitHub Release the workflow creates from the tag.

## Argument

`$ARGUMENTS` is one of:
- (empty) — auto-detect bump from commits since the last tag
- `test` — dry-run: print the plan, do nothing
- `patch` / `minor` / `major` — force that bump
- `0.2.0` (or any `X.Y.Z`) — set this exact version

## Step 1 — Sanity checks

Run all four. If any fails, stop and report to SACRVM — do not proceed.

```bash
git rev-parse --abbrev-ref HEAD                                    # must be 'main'
test -z "$(git status --porcelain)" && echo clean || echo dirty    # must be 'clean'
git fetch origin main --quiet
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" && echo synced || echo behind  # must be 'synced'
test -f src/Firepit/Firepit.csproj && echo found || echo missing   # must be 'found'
```

## Step 2 — Determine current state

```bash
git describe --tags --match 'v*' --abbrev=0 2>/dev/null || echo v0.0.0   # last v* tag
grep -oP '(?<=<Version>)[^<]+' src/Firepit/Firepit.csproj                # csproj version
git log $(git describe --tags --match 'v*' --abbrev=0 2>/dev/null || echo)..HEAD --format='%s'  # commits since last tag
```

If `git describe` finds no tag, treat last tag as `v0.0.0` and analyze all commits since the start.

## Step 3 — Decide the bump

Parse `$ARGUMENTS`:

| Arg | Action |
|---|---|
| empty | classify commits: any `feat:` or `V1.x:` introducing user-visible features → **minor**; only `fix:`/`build:`/`chore:` → **patch**; only `docs:`/`refactor:`/`test:` → **nothing to release**, stop here |
| `test` | same classification as empty, but stop after the plan in Step 4 |
| `patch` | force Z+1 |
| `minor` | force Y+1, Z=0 |
| `major` | force X+1, Y=0, Z=0 (pre-1.0 — confirm twice; usually wrong) |
| `X.Y.Z` | use literally — must be greater than current |

Compute `new_version` from the **csproj `<Version>`** (not from the tag — they may differ if a previous release wasn't tagged or vice versa).

## Step 4 — Show the plan, ask for confirmation

Print to SACRVM in German, exactly like:

```
Letzter Tag:           v0.1.0
Firepit.csproj <Version>: 0.1.0
Commits seit Tag:      12 (3 V1.x feature, 4 fix, 5 chore/docs/etc.)
Vorgeschlagener Bump:  minor (weil V1.x feature commits drin sind)
Neue Version:          0.2.0
Neuer Tag:             v0.2.0
```

Then list the commits since the last tag, grouped by prefix (V1.x / feat / fix / chore / docs / refactor / build / other), as a sanity check.

**If `$ARGUMENTS` is `test`: stop here. Do not change any files. Tell SACRVM „Trockenlauf — nichts geändert."**

Otherwise: ask **„OK so? [j/n]"** and wait for her answer.
- `j` / `ja` / `y` / `yes` → continue to Step 5
- anything else → abort, change nothing

## Step 5 — Bump csproj version

Edit `src/Firepit/Firepit.csproj`: change `<Version>OLD</Version>` to `<Version>NEW</Version>`. Use the Edit tool with the full surrounding `<Version>…</Version>` string for uniqueness.

Do the same in `tools/firepit-mcp/Firepit.Mcp.Bridge.csproj` — the bridge exe
carries its own version resource, and a stale one makes `firepit-mcp.exe`
report a different version than the MCP handshake does.

Also update the installer fallback in `installer/firepit.iss`:

```
#ifndef AppVersion
  #define AppVersion "NEW"
#endif
```

(CI overrides this via `/DAppVersion`, but keeping the fallback in sync means a local ISCC run also picks up the right number.)

## Step 6 — Sanity build

```bash
dotnet build Firepit.slnx --configuration Release --nologo
```

Catches compile errors locally before the tag goes out. If it fails, **stop**, show the error to SACRVM, leave the working tree as-is so she can inspect — do not commit, do not tag, do not push.

(The release workflow runs `dotnet test` on the runner, so we skip the test pass here for speed. If she wants the full gate locally, she can run `dotnet test` manually before invoking `/release`.)

## Step 7 — Commit, tag, push

```bash
git add src/Firepit/Firepit.csproj installer/firepit.iss
git commit -m "release: vNEW"
git tag vNEW
git push origin main
git push origin vNEW
```

Use the standard commit-message HEREDOC pattern; include the `Co-Authored-By` trailer per repo convention.

## Step 8 — Hand off

Tell SACRVM in German:

```
Release vNEW ist raus.
- Tag gepusht → ci.yml release-Job läuft
- Single-file Firepit.exe (~160 MB) wird publishd, gezippt und an Release angehängt
- Inno Setup installer wird via choco+ISCC gebaut und ebenfalls angehängt
- GitHub Release wird automatisch erstellt mit auto-generated release notes
- CI-Status: https://github.com/SACRVM/firepit-ai/actions
```

Do **not** poll or wait for the CI run — just hand off.

## Hard rules

- Never push tags without Step 4 confirmation.
- Never use `--no-verify` or `--force` on any git command.
- Never touch `.csproj` fields other than `<Version>`.
- The `/release` invocation is the explicit user authorization for the tag push and the public GitHub Release that follows.
- If anything is unclear or smells wrong (e.g., no commits since last tag, csproj/tag version mismatch, weird state), stop and ask SACRVM instead of guessing.
