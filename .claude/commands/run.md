---
description: Build and launch the Firepit WPF app via run.ps1 — kills any running instance, builds, launches the correct exe.
argument-hint: [release|nobuild|clean|combinations]
---

# /run — build + launch Firepit

You are launching Firepit for SACRVM to test. Use the repo-root `run.ps1`
script — never invoke `dotnet build` + `Firepit.exe` yourself, because the
build output path moved in V1.12 and the old TFM-suffixed paths still exist
on disk and trap unwary launches.

`run.ps1` always:

- Kills any running `Firepit.exe` (releases the file lock + singleton mutex)
- Builds (incremental, default Debug for speed)
- Launches `src/Firepit/bin/{Debug|Release}/Firepit.exe` — the canonical path
  guaranteed by `Directory.Build.props` (`AppendTargetFrameworkToOutputPath=false`)
- Starts the app **detached** (Start-Process), so the shell returns immediately

## Argument

`$ARGUMENTS` is a space-separated list of zero or more switches:

| Token | Maps to | Effect |
|---|---|---|
| (empty) | `./run.ps1` | Debug build + run (default — fast iteration) |
| `release` | `./run.ps1 -Release` | Release build + run (realistic perf) |
| `nobuild` | `./run.ps1 -NoBuild` | Skip build, just relaunch the existing exe |
| `clean` | `./run.ps1 -Clean` | Wipe stale TFM/RID output dirs first, then build+run |

Combinations are allowed: `release clean` → `./run.ps1 -Release -Clean`.
`nobuild` cannot combine with `clean` (clean implies a fresh build is wanted).

## Step 1 — translate args to switches

Parse `$ARGUMENTS`:
- Split on whitespace, lowercase each token.
- Build the PowerShell switch list. Reject unknown tokens with a one-line
  error to SACRVM (in German) and stop. Do NOT silently drop unknowns.
- If both `nobuild` and `clean` appear, stop and ask SACRVM which one she
  meant — they're mutually exclusive in intent.

## Step 2 — run the script

Invoke via the Bash tool with PowerShell:

```bash
powershell -ExecutionPolicy Bypass -File run.ps1 [switches]
```

Use the Bash tool (not the PowerShell tool) because `run.ps1` calls
`Start-Process` to launch the WPF app **detached**, and we want the script's
own status output to flow back to the conversation while the app runs in
its own process. (The PowerShell tool with `run_in_background:false` would
also work, but Bash + PowerShell-as-CLI is the consistent shape used
elsewhere in this repo.)

## Step 3 — report

Tell SACRVM in German what happened, terse:

- If build failed: paste the last ~10 lines of the build error output and
  stop. Do not try to "fix" the build — just surface it.
- If build succeeded and app launched: one line. „Läuft (Debug, build 4.2s)".
- If `-NoBuild` and the exe was missing: report it and suggest dropping the
  `nobuild` flag.

Never poll for the app to finish. The launch is fire-and-forget; SACRVM will
close the window when she's done.

## Hard rules

- Always invoke `run.ps1`. Never `dotnet build` + manual `Firepit.exe` start
  inline — that's exactly the trap this command exists to remove.
- If `run.ps1` doesn't exist at the repo root, stop and tell SACRVM — don't
  reinvent it inline.
- Don't add new switches to `run.ps1` from inside `/run` — if she asks for a
  new flag, edit `run.ps1` separately and update this command afterwards.
