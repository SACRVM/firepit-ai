using System.Windows;
using Firepit.Native;

namespace Firepit.Views;

/// <summary>
/// A single-line text-input dialog with inline validation. The optional
/// <c>validate</c> callback runs on every keystroke and on accept: it returns
/// an error string to display (and block accept), or <c>null</c> when the input
/// is valid. Returns the trimmed value on accept, or <c>null</c> if cancelled.
/// </summary>
public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validate;

    private InputDialog(
        string title,
        string message,
        string initial,
        string primaryLabel,
        string secondaryLabel,
        Func<string, string?>? validate)
    {
        InitializeComponent();
        _validate = validate;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageScroll.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primaryLabel;
        SecondaryButton.Content = secondaryLabel;
        InputBox.Text = initial;
        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;
        }
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
            Validate();
        };
    }

    public static string? Show(
        Window? owner,
        string title,
        string message,
        string initial = "",
        string primaryLabel = "Create",
        string secondaryLabel = "Cancel",
        Func<string, string?>? validate = null,
        double width = MessageDialog.DefaultWidth)
    {
        var dialog = new InputDialog(title, message, initial, primaryLabel, secondaryLabel, validate);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        DialogSizing.Apply(dialog, width);
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    /// <summary>Runs the validator and reflects the result in the error label and
    /// the primary button's enabled state. Returns true when input is valid.</summary>
    private bool Validate()
    {
        var error = _validate?.Invoke(InputBox.Text.Trim());
        if (string.IsNullOrEmpty(error))
        {
            ErrorText.Visibility = Visibility.Collapsed;
            PrimaryButton.IsEnabled = true;
            return true;
        }
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        return false;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (!Validate())
        {
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
