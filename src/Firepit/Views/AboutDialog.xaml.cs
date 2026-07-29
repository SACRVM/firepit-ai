using System.Reflection;
using System.Windows;
using Firepit.Native;

namespace Firepit.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ResolveVersion()}";
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
