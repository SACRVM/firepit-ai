using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Firepit.Core.Updates;
using Serilog;

namespace Firepit.Updates;

/// <summary>
/// Downloads a release's installer and hands off to it so Firepit can update
/// itself while running. The hand-off works around Windows file-locking: a
/// detached PowerShell helper waits for this process to exit, runs the Inno
/// installer silently (an over-install into the same per-user directory — no
/// UAC, since the installer is <c>PrivilegesRequired=lowest</c>), then relaunches
/// Firepit. Self-update only applies to installer builds; a portable copy
/// (no <c>unins000.exe</c> alongside the exe) falls back to the browser.
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>
    /// True if this build was installed by the Inno setup (Inno drops
    /// <c>unins000.exe</c> next to the exe). <paramref name="installDir"/> is
    /// the directory to update + relaunch from.
    /// </summary>
    public static bool TryGetInnoInstallDir(out string installDir)
    {
        installDir = string.Empty;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return false;
        if (!File.Exists(Path.Combine(dir, "unins000.exe"))) return false;
        installDir = dir;
        return true;
    }

    /// <summary>
    /// Download the installer asset into <c>%LOCALAPPDATA%\Firepit\updates</c>.
    /// Verifies the byte count against the release metadata when known.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.InstallerAssetUrl))
        {
            throw new InvalidOperationException("Release has no installer asset to download.");
        }

        var dir = Path.Combine(Firepit.Core.FirepitPaths.Local, "updates");
        Directory.CreateDirectory(dir);

        var name = string.IsNullOrEmpty(info.InstallerAssetName)
            ? $"FirepitSetup-{info.Version.ToString(3)}-win-x64.exe"
            : info.InstallerAssetName;
        var dest = Path.Combine(dir, name);

        using (var resp = await http.GetAsync(info.InstallerAssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = File.Create(dest);
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        if (info.InstallerAssetSize > 0)
        {
            var len = new FileInfo(dest).Length;
            if (len != info.InstallerAssetSize)
            {
                throw new IOException($"Downloaded installer is {len} bytes, expected {info.InstallerAssetSize}.");
            }
        }

        Log.Information("Update: downloaded installer to {Path}", dest);
        return dest;
    }

    /// <summary>
    /// Spawn the detached helper that performs the swap, then shut Firepit down
    /// so its files unlock. The helper relaunches Firepit when the installer
    /// finishes. Must be called on the UI thread (it calls Application.Shutdown).
    /// </summary>
    public static void LaunchAndExit(string installerPath, string installDir)
    {
        var exe = Path.Combine(installDir, "Firepit.exe");
        var pid = Environment.ProcessId;

        // PowerShell over a .bat: Wait-Process is a clean "wait for our exit"
        // primitive and -EncodedCommand sidesteps all path-quoting pitfalls.
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id {{pid}} -Timeout 90 } catch { }
Start-Process -FilePath '{{Esc(installerPath)}}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait
Start-Process -FilePath '{{Esc(exe)}}'
""";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Log.Information("Update: hand-off helper launched (waiting on pid {Pid}), shutting down for {Installer}", pid, installerPath);
        Application.Current.Shutdown();
    }

    // Single-quote escaping for PowerShell single-quoted string literals.
    private static string Esc(string s) => s.Replace("'", "''");
}
