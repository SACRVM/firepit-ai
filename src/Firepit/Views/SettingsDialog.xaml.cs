using System;
using System.Windows;
using Firepit.Core.Settings;
using Firepit.Native;
using Microsoft.Win32;

namespace Firepit.Views;

public partial class SettingsDialog : Window
{
    private readonly ISettingsStore _store;
    private FirepitSettings _settings;
    private int _currentFontSize;

    public SettingsDialog(ISettingsStore store)
    {
        InitializeComponent();
        _store = store;
        _settings = store.Load();
        RootBox.Text = _settings.ProjectsRoot;
        _currentFontSize = (_settings.Ui ?? UiSettings.Defaults).ResolvedFontSize;
        UpdateFontSizeDisplay();
        ApplyChromeMetricsFromResources();
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);
    }

    /// <summary>
    /// Design width in DIPs. Callers hand this to <see cref="DialogSizing"/>
    /// once <see cref="Window.Owner"/> is set — sizing has to happen before the
    /// window is shown, so the dialog can't do it for itself.
    /// </summary>
    internal const double DesignWidth = 540;

    public FirepitSettings? Result { get; private set; }

    private void ApplyChromeMetricsFromResources()
    {
        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;
        }
    }

    private void UpdateFontSizeDisplay()
    {
        FontSizeValue.Text = _currentFontSize.ToString();
        FontSizeMinus.IsEnabled = _currentFontSize > UiSettings.MinFontSize;
        FontSizePlus.IsEnabled = _currentFontSize < UiSettings.MaxFontSize;
    }

    private void OnFontSizeMinus(object sender, RoutedEventArgs e)
    {
        if (_currentFontSize > UiSettings.MinFontSize) { _currentFontSize--; UpdateFontSizeDisplay(); }
    }

    private void OnFontSizePlus(object sender, RoutedEventArgs e)
    {
        if (_currentFontSize < UiSettings.MaxFontSize) { _currentFontSize++; UpdateFontSizeDisplay(); }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = RootBox.Text,
            Title = "Select projects root",
        };
        if (dialog.ShowDialog(this) == true)
        {
            RootBox.Text = dialog.FolderName;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var updated = _settings with
        {
            ProjectsRoot = RootBox.Text.Trim(),
            Ui = new UiSettings(_currentFontSize),
        };
        try
        {
            _store.Save(updated);
            Result = updated;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not save settings", ex.Message);
        }
    }

    private void OnOpenJsonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.File.Exists(_store.SettingsPath))
            {
                _store.Save(_settings);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_store.SettingsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not open settings.json", ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
