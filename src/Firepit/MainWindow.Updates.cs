using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Firepit.Core.Settings;
using Firepit.Core.Updates;
using Firepit.Updates;
using Firepit.Views;
using Serilog;

namespace Firepit;

/// <summary>
/// Background update checking. Polls GitHub Releases (startup + on an interval),
/// shows the caption-bar ember badge when a newer version lands, and drives the
/// install/ignore/later dialog. The network call is <see cref="GitHubUpdateChecker"/>
/// (Core); the download + installer hand-off is <see cref="UpdateInstaller"/>.
/// </summary>
public partial class MainWindow
{
    private const string UpdateOwner = "SACRVM";
    private const string UpdateRepo = "firepit-ai";

    // One shared client for both the API check and the installer download.
    private static readonly HttpClient UpdateHttp = CreateUpdateHttp();

    private DispatcherTimer? _updateTimer;
    private IUpdateChecker? _updateChecker;
    private UpdateInfo? _availableUpdate;
    private bool _updateInstallInProgress;

    // Why the most recent attempt failed, or null if it succeeded. Without
    // this the About dialog would report a silently failing background check
    // as "up to date" — the exact thing it was added to stop.
    private string? _lastUpdateError;

    private static HttpClient CreateUpdateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Firepit-Updater");
        return http;
    }

    /// <summary>
    /// Wire up the background checks. Safe to call once from OnLoaded. No-op
    /// when the user has opted out via settings.json (<c>updates.checkForUpdates=false</c>).
    /// </summary>
    private void StartUpdateChecks()
    {
        var cfg = _settings.Updates ?? UpdateSettings.Defaults;
        if (!cfg.CheckForUpdates)
        {
            Log.Information("Update checks disabled in settings");
            return;
        }

        var current = typeof(MainWindow).Assembly.GetName().Version;
        if (current is null) return;

        _updateChecker = new GitHubUpdateChecker(UpdateHttp, UpdateOwner, UpdateRepo,
            log: m => Log.Information("Update: {Message}", m));

        // First check shortly after launch so it never races the cold-start
        // WebView2 boot; then on the configured interval.
        _ = RunUpdateCheckAsync(current, TimeSpan.FromSeconds(20));

        var hours = Math.Max(1, cfg.CheckIntervalHours);
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(hours),
        };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync(current, TimeSpan.Zero);
        _updateTimer.Start();
    }

    private async Task RunUpdateCheckAsync(Version current, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(true);
        }

        await CheckForUpdateAsync(respectIgnoredVersion: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Asks GitHub whether a newer release exists, and records the answer —
    /// including a failure, which the badge alone cannot express.
    /// </summary>
    /// <param name="respectIgnoredVersion">
    /// False when the user asked explicitly. Someone who presses "Check now"
    /// wants the current answer, not the one they silenced three versions ago.
    /// </param>
    internal async Task<UpdateCheckOutcome> CheckForUpdateAsync(bool respectIgnoredVersion)
    {
        var current = typeof(MainWindow).Assembly.GetName().Version;
        var checker = _updateChecker ??= current is null
            ? null
            : new GitHubUpdateChecker(UpdateHttp, UpdateOwner, UpdateRepo,
                log: m => Log.Information("Update: {Message}", m));

        if (checker is null || current is null)
        {
            return new UpdateCheckOutcome(null, "This build carries no version to compare.", LastSuccessfulCheck());
        }

        try
        {
            var info = await checker.CheckAsync(current, CancellationToken.None).ConfigureAwait(true);
            var now = DateTimeOffset.UtcNow;
            _lastUpdateError = null;
            RecordSuccessfulCheck(now);
            if (_disposedUpdates)
            {
                return new UpdateCheckOutcome(info, null, now);
            }

            if (info is null)
            {
                Log.Information("Update check: {Current} is current", current);
                return new UpdateCheckOutcome(null, null, now);
            }

            var ignored = (_settings.Updates ?? UpdateSettings.Defaults).IgnoredVersion;
            if (respectIgnoredVersion
                && ignored is not null
                && Version.TryParse(ignored, out var iv)
                && info.Version <= new Version(iv.Major, iv.Minor, Math.Max(0, iv.Build)))
            {
                Log.Information("Update {Version} available but ignored by user", info.Version);
                return new UpdateCheckOutcome(null, null, now);
            }

            Log.Information("Update available: {Version} (current {Current})", info.Version, current);
            ShowUpdateBadge(info);
            return new UpdateCheckOutcome(info, null, now);
        }
        catch (Exception ex)
        {
            // A failed update check must never disrupt the app — but it must
            // also stop being invisible. The badge cannot show a failure, so
            // the outcome carries it to whoever asked.
            Log.Information(ex, "Update check failed (non-fatal)");
            _lastUpdateError = ex.Message;
            return new UpdateCheckOutcome(null, ex.Message, LastSuccessfulCheck());
        }
    }

    /// <summary>
    /// What the background checks already know, so the About dialog can answer
    /// immediately instead of making the user wait for a round trip to say
    /// something it could have said at once.
    /// </summary>
    private UpdateCheckOutcome KnownUpdateState()
    {
        var cfg = _settings.Updates ?? UpdateSettings.Defaults;
        if (!cfg.CheckForUpdates)
        {
            return new UpdateCheckOutcome(
                null, "Automatic checks are off (updates.checkForUpdates in settings.json).",
                LastSuccessfulCheck());
        }

        return new UpdateCheckOutcome(_availableUpdate, _lastUpdateError, LastSuccessfulCheck());
    }

    private DateTimeOffset? LastSuccessfulCheck()
    {
        var raw = (_settings.Updates ?? UpdateSettings.Defaults).LastSuccessfulCheckUtc;
        return DateTimeOffset.TryParse(
            raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }

    private void RecordSuccessfulCheck(DateTimeOffset at)
    {
        var updates = _settings.Updates ?? UpdateSettings.Defaults;
        _settings = _settings with
        {
            Updates = updates with { LastSuccessfulCheckUtc = at.ToString("O") },
        };
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            // Losing the timestamp costs a slightly vaguer message later, not
            // an update.
            Log.Debug(ex, "Could not persist the last update-check time");
        }
    }

    private void ShowUpdateBadge(UpdateInfo info)
    {
        _availableUpdate = info;
        UpdateLabel.Text = $"v{info.Version.ToString(3)}";
        UpdateButton.ToolTip = $"Firepit {info.Version.ToString(3)} is ready — click for details";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private void OnUpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } info || _updateInstallInProgress) return;

        var current = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? string.Empty
            : "\n\n" + Truncate(info.ReleaseNotes!.Trim(), 600);
        var canSelfUpdate = !string.IsNullOrEmpty(info.InstallerAssetUrl)
                            && UpdateInstaller.TryGetInnoInstallDir(out _);

        var primary = canSelfUpdate ? "Update & restart" : "Open in browser";
        var message =
            $"Installed:  {current}\n" +
            $"Available:  {info.Version.ToString(3)}" +
            (canSelfUpdate
                ? "\n\nFirepit will be downloaded, closed, and restarted. Any running agent sessions will be terminated."
                : "\n\nThis installation can't update itself automatically — the release page will open in your browser.") +
            notes;

        var choice = MessageDialog.ShowChoice(
            this,
            title: $"Update available: v{info.Version.ToString(3)}",
            message: message,
            primaryLabel: primary,
            secondaryLabel: "Ignore this version",
            // Release notes are the longest body we show — give them room.
            width: 640);

        switch (choice)
        {
            case MessageChoice.Primary:
                _ = InstallUpdateAsync(info, canSelfUpdate);
                break;
            case MessageChoice.Secondary:
                IgnoreUpdate(info);
                break;
            case MessageChoice.Dismissed:
                // "Later" — leave the badge up, ask again next check.
                break;
        }
    }

    private void IgnoreUpdate(UpdateInfo info)
    {
        var current = _settings.Updates ?? UpdateSettings.Defaults;
        _settings = _settings with { Updates = current with { IgnoredVersion = info.Version.ToString(3) } };
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist ignored update version");
        }
        _availableUpdate = null;
        UpdateButton.Visibility = Visibility.Collapsed;
    }

    private async Task InstallUpdateAsync(UpdateInfo info, bool canSelfUpdate)
    {
        if (!canSelfUpdate)
        {
            OpenReleasePage(info.ReleaseUrl);
            return;
        }

        if (!UpdateInstaller.TryGetInnoInstallDir(out var installDir))
        {
            OpenReleasePage(info.ReleaseUrl);
            return;
        }

        _updateInstallInProgress = true;
        try
        {
            ShowToast($"Downloading Firepit {info.Version.ToString(3)} …");
            var installerPath = await UpdateInstaller.DownloadAsync(info, UpdateHttp, CancellationToken.None).ConfigureAwait(true);
            // Hands off to the detached helper and shuts Firepit down. OnClosing
            // still runs first (tabs persisted, sessions disposed cleanly).
            UpdateInstaller.LaunchAndExit(installerPath, installDir);
        }
        catch (Exception ex)
        {
            _updateInstallInProgress = false;
            Log.Error(ex, "Update install failed");
            ShowToast($"Update failed: {ex.Message}", isError: true);
        }
    }

    private void OpenReleasePage(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open release page {Url}", url);
            ShowToast("Couldn't open the release page.", isError: true);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + " …";

    // Set in OnClosing so a late-returning update check doesn't touch a
    // tearing-down window.
    private bool _disposedUpdates;
}
