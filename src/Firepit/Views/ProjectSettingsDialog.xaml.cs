using System;
using System.IO;
using System.Windows;
using Firepit.Knowledge;
using Firepit.Native;
using Microsoft.Win32;

namespace Firepit.Views;

/// <summary>
/// Per-project settings. Today it owns one thing: where the project's
/// knowledge lives — the setting that otherwise means hand-writing a path into
/// <c>.firepit/knowledge</c> and getting the relative part right.
/// </summary>
public partial class ProjectSettingsDialog : Window
{
    internal const double DesignWidth = 600;

    private readonly string _projectPath;
    private readonly string _projectName;
    private readonly string _metaProjectPath;
    private bool _loaded;

    /// <summary>True when the knowledge location changed and scopes need a resync.</summary>
    public bool KnowledgeChanged { get; private set; }

    public ProjectSettingsDialog(string projectName, string projectPath, string metaProjectPath)
    {
        InitializeComponent();
        _projectName = projectName;
        _projectPath = projectPath;
        _metaProjectPath = metaProjectPath;

        CaptionText.Text = $"Project settings — {projectName}";
        ProjectPathText.Text = projectPath;
        InRepoHint.Text = KnowledgeLayout.LocalDocsDir(projectPath);
        HostedHint.Text = KnowledgeLayout.HostedDocsDir(metaProjectPath, projectName);

        LoadCurrentChoice();
        _loaded = true;
        UpdateStatus();

        ApplyChromeMetricsFromResources();
        SourceInitialized += (_, _) => WindowDarkMode.EnableForWindow(this);
    }

    private void ApplyChromeMetricsFromResources()
    {
        if (TryFindResource("DialogCaptionPixelHeight") is double capH)
        {
            CaptionRow.Height = new GridLength(capH);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = capH;
        }
    }

    private void LoadCurrentChoice()
    {
        var resolved = KnowledgeLocator.Resolve(_projectPath);
        var hosted = KnowledgeLayout.HostedDocsDir(_metaProjectPath, _projectName);

        if (!resolved.IsRedirected && resolved.Error is null)
        {
            InRepoChoice.IsChecked = true;
            CustomPathBox.Text = hosted;
            return;
        }

        // A broken pointer still shows its target, so the path that needs
        // fixing is on screen rather than something to go hunting for.
        var current = resolved.Error is null ? resolved.DocsDir : ReadRawPointer() ?? hosted;
        CustomPathBox.Text = current;

        if (resolved.Error is null && PathsEqual(current, hosted))
        {
            HostedChoice.IsChecked = true;
        }
        else
        {
            CustomChoice.IsChecked = true;
        }
    }

    private string? ReadRawPointer()
    {
        try
        {
            var path = KnowledgeLayout.LocalDocsDir(_projectPath);
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim().Trim('"');
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    return Path.GetFullPath(
                        Path.Combine(Path.Combine(_projectPath, ".firepit"), trimmed));
                }
            }
        }
        catch (Exception)
        {
            // Unreadable pointer: fall through to the default suggestion.
        }

        return null;
    }

    private string? SelectedTarget()
    {
        if (InRepoChoice.IsChecked == true)
        {
            return null;
        }

        if (HostedChoice.IsChecked == true)
        {
            return KnowledgeLayout.HostedDocsDir(_metaProjectPath, _projectName);
        }

        var custom = CustomPathBox.Text.Trim();
        return custom.Length == 0 ? null : custom;
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (HostedChoice.IsChecked == true)
        {
            CustomPathBox.Text = KnowledgeLayout.HostedDocsDir(_metaProjectPath, _projectName);
        }

        UpdateStatus();
    }

    private void OnCustomPathChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        try
        {
            var target = SelectedTarget();
            var plan = KnowledgePointerFile.Describe(_projectPath, target);
            SaveButton.IsEnabled = plan.CanApply;

            if (plan.Blocker is { } blocker)
            {
                StatusText.Text = blocker;
                StatusBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x7A, 0x3B, 0x2E));
                return;
            }

            StatusBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x33, 0x2B, 0x22));

            var lines = new System.Collections.Generic.List<string> { plan.TargetDir };
            if (plan.DocumentsToMove > 0)
            {
                lines.Add(plan.DocumentsToMove == 1
                    ? "1 document will be moved there."
                    : $"{plan.DocumentsToMove} documents will be moved there.");
            }

            if (target is not null)
            {
                lines.Add(
                    "Not committed with this repo. knowledge-pinned.md stays here and is " +
                    "hidden from git via .git/info/exclude.");
            }

            StatusText.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = ex.Message;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the knowledge folder",
            InitialDirectory = Directory.Exists(CustomPathBox.Text) ? CustomPathBox.Text : _metaProjectPath,
        };
        if (dialog.ShowDialog(this) == true)
        {
            CustomChoice.IsChecked = true;
            CustomPathBox.Text = dialog.FolderName;
        }
    }

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = KnowledgeLocator.Resolve(_projectPath).DocsDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not open the folder", ex.Message);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            KnowledgePointerFile.Apply(_projectPath, SelectedTarget());
            KnowledgeChanged = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Could not change the knowledge location", ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
