# Firepit dev runner -- kills any running instance, builds, and launches the
# correct exe in one step. Eliminates "which exe is the freshest one" guessing
# by always pointing to src/Firepit/bin/{Config}/Firepit.exe (the path that
# Directory.Build.props guarantees via AppendTargetFrameworkToOutputPath=false).
#
# Runs as a SEPARATE instance ('dev') by default, so it can be started next to
# the installed Firepit -- including one that is hosting an agent session --
# without disturbing it. Its settings, state, logs and browser profile live in
# Firepit-dev alongside the real ones; nothing is shared.
#
# Usage:
#   ./run.ps1                # Debug build + run as the 'dev' instance
#   ./run.ps1 -Release       # Release build + run (realistic perf)
#   ./run.ps1 -NoBuild       # Skip build, just launch the existing exe
#   ./run.ps1 -Clean         # Wipe stale TFM/RID output dirs first, then build+run
#   ./run.ps1 -Instance beta # Some other named instance, its own data again
#   ./run.ps1 -Instance ''   # Run as the DEFAULT instance -- kills an installed
#                            # Firepit that is running, agent session included

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$NoBuild,
    [switch]$Clean,
    # Which Firepit to be. Defaults to 'dev': a separate instance with its own
    # settings, state, logs and browser profile, which can run beside the
    # installed Firepit instead of replacing it. Pass -Instance '' to run as
    # the default instance -- that one DOES kill an installed Firepit that is
    # already running, including the one hosting an agent session.
    [string]$Instance = 'dev'
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$config   = if ($Release) { 'Release' } else { 'Debug' }
$exePath  = Join-Path $repoRoot "src/Firepit/bin/$config/Firepit.exe"

function Write-Status($msg) { Write-Host "[run.ps1] $msg" -ForegroundColor Cyan }

# 1. Kill the running copy of THIS instance -- releases the file lock on
#    Firepit.exe and its singleton pipe. Deliberately not every Firepit: this
#    script used to kill them all, which included the installed one, which
#    included the window an agent session was running in. Testing a change
#    therefore meant destroying the session making it, and the UI shipped
#    unlooked-at. Matching on the command line keeps the two apart.
$firepitProcs = @(Get-CimInstance Win32_Process -Filter "Name='Firepit.exe'" -ErrorAction SilentlyContinue)
if ($Instance) {
    $pattern = "--instance[=\s]+$([regex]::Escape($Instance))(\s|$)"
    $running = @($firepitProcs | Where-Object { $_.CommandLine -match $pattern })
    $label   = "'$Instance' instance"
} else {
    $running = @($firepitProcs | Where-Object { $_.CommandLine -notmatch '--instance' })
    $label   = 'default instance'
}
if ($running.Count -gt 0) {
    Write-Status "Killing $($running.Count) running Firepit ($label)..."
    $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600
}
$survivors = @($firepitProcs).Count - $running.Count
if ($survivors -gt 0) {
    Write-Status "Leaving $survivors other Firepit instance(s) alone."
}

# 2. Optional: clear stale TFM/RID output dirs left from pre-V1.12 builds where
#    AppendTargetFrameworkToOutputPath was still true. Those stale exes are the
#    classic "which one did I just launch?" trap.
if ($Clean) {
    $staleDirs = @(
        "src/Firepit/bin/Debug/net10.0-windows10.0.17763.0",
        "src/Firepit/bin/Release/net10.0-windows10.0.17763.0",
        "src/Firepit/bin/Debug/win-x64",
        "src/Firepit/bin/Release/win-x64"
    )
    foreach ($d in $staleDirs) {
        $full = Join-Path $repoRoot $d
        if (Test-Path $full) {
            Write-Status "Removing stale $d"
            Remove-Item $full -Recurse -Force
        }
    }
}

# 3. Build (unless explicitly skipped). dotnet build is incremental -- if nothing
#    changed it returns in ~1 s, so we don't gate on `-NoBuild` for speed alone.
if (-not $NoBuild) {
    Write-Status "Building $config..."
    $buildStart = [DateTime]::UtcNow
    & dotnet build (Join-Path $repoRoot 'Firepit.slnx') --configuration $config --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Status "Build FAILED (exit $LASTEXITCODE) -- not launching."
        exit $LASTEXITCODE
    }
    $buildSecs = [Math]::Round(([DateTime]::UtcNow - $buildStart).TotalSeconds, 1)
    Write-Status "Build OK in $buildSecs s"
}

# 4. Launch. Start-Process so this script returns immediately (the user's shell
#    is freed up; the WPF app runs detached).
if (-not (Test-Path $exePath)) {
    Write-Status "EXE not found: $exePath"
    Write-Status "Run without -NoBuild to build it first."
    exit 1
}
$builtAge = [Math]::Round(([DateTime]::UtcNow - (Get-Item $exePath).LastWriteTimeUtc).TotalSeconds, 0)
if ($Instance) {
    Write-Status "Launching src/Firepit/bin/$config/Firepit.exe as instance '$Instance' (built $builtAge s ago)"
    Start-Process $exePath -ArgumentList '--instance', $Instance
} else {
    Write-Status "Launching src/Firepit/bin/$config/Firepit.exe as the DEFAULT instance (built $builtAge s ago)"
    Start-Process $exePath
}
