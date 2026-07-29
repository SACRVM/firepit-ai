using System.Windows;
using Firepit.Native;

namespace Firepit.Views;

/// <summary>
/// Outcome of a three-way dialog. <see cref="Dismissed"/> means the user closed
/// the dialog (X / clicked away) without choosing either action — distinct from
/// <see cref="Secondary"/>, which is a deliberate "no/decline" click.
/// </summary>
public enum MessageChoice
{
    Primary,
    Secondary,
    Dismissed,
}

public partial class MessageDialog : Window
{
    /// <summary>
    /// Design width in DIPs — scaled by the UI font setting and capped to the
    /// screen by <see cref="DialogSizing"/>.
    /// </summary>
    public const double DefaultWidth = 560;

    private MessageChoice _choice = MessageChoice.Dismissed;

    private MessageDialog(string title, string message, string primaryLabel, string? secondaryLabel)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        if (secondaryLabel is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.IsCancel = true;
        }
        else
        {
            SecondaryButton.Content = secondaryLabel;
        }
        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;
        }
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string primaryLabel = "OK",
        string? secondaryLabel = null,
        double width = DefaultWidth)
    {
        var dialog = new MessageDialog(title, message, primaryLabel, secondaryLabel);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        DialogSizing.Apply(dialog, width);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Three-way variant: distinguishes Primary, Secondary, and Dismissed (X /
    /// Esc). Used where "decline" and "close without deciding" mean different
    /// things — e.g. an update prompt's "ignore this version" vs "ask me later".
    /// </summary>
    public static MessageChoice ShowChoice(
        Window? owner,
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        double width = DefaultWidth)
    {
        var dialog = new MessageDialog(title, message, primaryLabel, secondaryLabel);
        // Esc must mean "later" (Dismissed), not the secondary action — so the
        // secondary button must not double as the cancel button here.
        dialog.SecondaryButton.IsCancel = false;
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        DialogSizing.Apply(dialog, width);
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        _choice = MessageChoice.Primary;
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        _choice = MessageChoice.Secondary;
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _choice = MessageChoice.Dismissed;
        DialogResult = false;
        Close();
    }
}
