using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Firepit.Core.Updates;
using Firepit.Native;

namespace Firepit.Views;

public partial class AboutDialog : Window
{
    private readonly Func<Task<UpdateCheckOutcome>>? _check;
    private readonly Action<UpdateInfo>? _install;
    private UpdateInfo? _pending;
    private bool _busy;

    /// <param name="check">Asks GitHub. Null leaves the update section hidden.</param>
    /// <param name="install">Runs the update the check found.</param>
    public AboutDialog(
        Func<Task<UpdateCheckOutcome>>? check = null,
        Action<UpdateInfo>? install = null,
        UpdateCheckOutcome? lastKnown = null)
    {
        InitializeComponent();
        _check = check;
        _install = install;
        VersionText.Text = $"Version {ResolveVersion()}";

        if (check is null)
        {
            UpdateStateText.Visibility = Visibility.Collapsed;
            UpdateActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Render(lastKnown);
        }

        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;
        }
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);
    }

    /// <summary>
    /// Design width in DIPs. Callers hand this to <see cref="DialogSizing"/>
    /// once <see cref="Window.Owner"/> is set — sizing has to happen before the
    /// window is shown, so the dialog can't do it for itself.
    /// </summary>
    internal const double DesignWidth = 380;

    private static string ResolveVersion()
    {
        // <Version> in Firepit.csproj flows into AssemblyInformationalVersion at build time.
        // That's the canonical user-facing version; AssemblyVersion is padded to 4 parts.
        var info = typeof(AboutDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // SourceLink appends "+sha"; strip it for display.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary>
    /// Shows one of four states. The fourth — the check itself failing — is the
    /// reason this exists: the caption-bar badge only ever appears on good
    /// news, so an installation whose checks have been failing for weeks looks
    /// exactly like one that is up to date.
    /// </summary>
    private void Render(UpdateCheckOutcome? outcome)
    {
        _pending = outcome?.Update;

        if (outcome is null)
        {
            UpdateStateText.Text = "Update state unknown.";
            UpdateActionButton.Content = "Check now";
            return;
        }

        if (outcome.Update is { } update)
        {
            UpdateStateText.Text = $"↑  {update.Version.ToString(3)} is available";
            UpdateActionButton.Content = "Update now";
            return;
        }

        if (outcome.Succeeded)
        {
            UpdateStateText.Text = $"✓  Up to date  ·  checked {Format(outcome.LastSuccessUtc)}";
            UpdateActionButton.Content = "Check now";
            return;
        }

        UpdateStateText.Text = outcome.LastSuccessUtc is { } last
            ? $"⚠  Could not check  ·  last succeeded {Format(last)}"
            : "⚠  Could not check, and never has on this install";
        UpdateActionButton.Content = "Try again";
    }

    private static string Format(DateTimeOffset? utc)
    {
        if (utc is not { } value)
        {
            return "never";
        }

        var local = value.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromDays(1)) return local.ToString("HH:mm");
        // Days matter more than the clock time once it has been a while —
        // "3 days ago" is the part that should make someone look.
        return age.TotalDays < 2 ? "yesterday" : $"{(int)age.TotalDays} days ago";
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_pending is { } update && _install is not null)
        {
            _install(update);
            DialogResult = false;
            Close();
            return;
        }

        if (_check is null) return;

        _busy = true;
        UpdateActionButton.IsEnabled = false;
        UpdateStateText.Text = "Checking…";
        try
        {
            Render(await _check());
        }
        catch (Exception ex)
        {
            Render(new UpdateCheckOutcome(null, ex.Message, null));
        }
        finally
        {
            _busy = false;
            UpdateActionButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
