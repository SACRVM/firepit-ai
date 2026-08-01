using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Firepit.Core.Artifacts;
using Firepit.Core.ProjectConfig;
using Firepit.Views;
using Serilog;

namespace Firepit;

/// <summary>
/// Artifact pane: a curated shelf of links to files the agent produced, so the
/// user stops alt-tabbing to Explorer to look at a report or run a build.
///
/// Two rules keep this from turning into the file browser CLAUDE.md forbids:
/// nothing enumerates a directory (entries only appear by an explicit act), and
/// the pane never copies or deletes a file — an entry is a link, removing it
/// unpins it and nothing more.
/// </summary>
public partial class MainWindow
{
    private bool _artifactPaneOpen;
    private bool _artifactPanePinned;

    /// <summary>Wired from the constructor: restores the pinned state.</summary>
    private void InitializeArtifactPane()
    {
        try
        {
            _artifactPanePinned = _stateStore.Load().ArtifactPanePinned;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read artifact pane state; defaulting to unpinned");
            _artifactPanePinned = false;
        }

        ArtifactPinButton.Tag = _artifactPanePinned ? "pinned" : null;
        if (_artifactPanePinned)
        {
            SetArtifactPaneOpen(true);
        }
    }

    private void OnArtifactPaneToggleClick(object sender, RoutedEventArgs e) =>
        SetArtifactPaneOpen(!_artifactPaneOpen);

    private void OnArtifactPaneCloseClick(object sender, RoutedEventArgs e)
    {
        // Closing by hand also drops the pin — otherwise the pane reopens on
        // the next launch and the close reads as ignored.
        if (_artifactPanePinned)
        {
            _artifactPanePinned = false;
            ArtifactPinButton.Tag = null;
            PersistArtifactPaneState();
        }
        SetArtifactPaneOpen(false);
    }

    private void OnArtifactPinClick(object sender, RoutedEventArgs e)
    {
        _artifactPanePinned = !_artifactPanePinned;
        ArtifactPinButton.Tag = _artifactPanePinned ? "pinned" : null;
        PersistArtifactPaneState();
        if (_artifactPanePinned && !_artifactPaneOpen)
        {
            SetArtifactPaneOpen(true);
        }
    }

    private void SetArtifactPaneOpen(bool open)
    {
        _artifactPaneOpen = open;
        ArtifactPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ArtifactPaneButton.Tag = open ? "open" : null;
        if (open)
        {
            RefreshArtifactPaneForActiveTab();
        }
    }

    private void PersistArtifactPaneState()
    {
        try
        {
            _stateStore.Save(_stateStore.Load() with { ArtifactPanePinned = _artifactPanePinned });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist artifact pane state");
        }
    }

    /// <summary>
    /// Re-render the pane for <paramref name="projectPath"/> — but only when
    /// that project is the one on screen. Called straight from the MCP handlers
    /// so a pinned artifact shows up the moment the tool returns.
    /// </summary>
    private void RefreshArtifactPane(string projectPath)
    {
        if (!_artifactPaneOpen)
        {
            return;
        }
        var active = (Tabs.SelectedItem as TabItem)?.Tag as SessionTab;
        if (active is null || !string.Equals(active.Context.Path, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        RenderArtifacts(active.Context.Path, active.Context.Name);
    }

    private void RefreshArtifactPaneForActiveTab()
    {
        if (!_artifactPaneOpen)
        {
            return;
        }
        if ((Tabs.SelectedItem as TabItem)?.Tag is SessionTab active)
        {
            RenderArtifacts(active.Context.Path, active.Context.Name);
        }
        else
        {
            ArtifactPaneTitle.Text = "Artefakte";
            ArtifactList.ItemsSource = null;
            ArtifactEmptyState.Text = "Kein Projekt offen.";
            ArtifactEmptyState.Visibility = Visibility.Visible;
        }
    }

    private void RenderArtifacts(string projectPath, string projectName)
    {
        IReadOnlyList<ArtifactEntry> entries;
        try
        {
            entries = _artifactStore.Load(projectPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load artifacts for {Project}", projectName);
            entries = [];
        }

        var items = ArtifactResolver.ResolveAll(entries, projectPath)
            .Select(a => new ArtifactItem(a))
            .ToList();

        ArtifactPaneTitle.Text = items.Count > 0 ? $"Artefakte · {items.Count}" : "Artefakte";
        ArtifactList.ItemsSource = items;
        ArtifactEmptyState.Text =
            "Noch nichts abgelegt. Der Agent kann hier Dateien anheften — Reports, Screenshots, Builds.";
        ArtifactEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- item actions -------------------------------------------------

    private static ArtifactItem? ItemFrom(object sender) => sender switch
    {
        Button { Tag: ArtifactItem item }                          => item,
        MenuItem menu when ContextItem(menu) is { } fromMenu       => fromMenu,
        _                                                          => null,
    };

    /// <summary>
    /// A ContextMenu is not in the visual tree of its owner, so the usual
    /// parent walk fails — the owning Button comes from PlacementTarget.
    /// </summary>
    private static ArtifactItem? ContextItem(MenuItem menu)
    {
        var parent = menu.Parent;
        while (parent is MenuItem outer)
        {
            parent = outer.Parent;
        }
        return (parent as ContextMenu)?.PlacementTarget is Button { Tag: ArtifactItem item } ? item : null;
    }

    private void OnArtifactItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item)
        {
            OpenArtifact(item);
        }
    }

    private void OnArtifactOpenClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item)
        {
            OpenArtifact(item);
        }
    }

    /// <summary>
    /// Open with the system handler — which for an executable means running it.
    /// That is the point: linking a demo build and starting it with one click is
    /// a first-class use of this pane, and the run icon in the list says so
    /// before the click rather than after.
    /// </summary>
    private void OpenArtifact(ArtifactItem item)
    {
        var path = item.Resolved.AbsolutePath;
        if (!item.Resolved.Exists)
        {
            ShowToast($"Artefakt fehlt: {path}", isError: true);
            return;
        }
        try
        {
            // Fully qualified: bare `Process` binds to the Firepit.Process
            // namespace from inside namespace Firepit.
            System.Diagnostics.Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log.Information("Artifact opened: {Kind} {Path}", item.Resolved.Kind, path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open artifact {Path}", path);
            ShowToast($"Konnte nicht öffnen: {ex.Message}", isError: true);
        }
    }

    private void OnArtifactRevealClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item)
        {
            return;
        }
        var path = item.Resolved.AbsolutePath;
        try
        {
            // /select, needs the path quoted as a single argument; a missing
            // file still opens the parent folder, which is the useful fallback.
            if (File.Exists(path) || Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(
                    new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                {
                    ShowToast("Ordner existiert nicht mehr", isError: true);
                    return;
                }
                System.Diagnostics.Process.Start(
                    new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reveal artifact {Path}", path);
            ShowToast($"Explorer-Fehler: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Promote a throwaway artifact link into a permanent toolbar button —
    /// the "this one earned its place" move. Executables only: a shell command
    /// is defined as command+args, and turning a PDF into one would produce a
    /// button whose behaviour depends on shell association rather than on
    /// anything the config states.
    /// </summary>
    private void OnArtifactPromoteClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item)
        {
            return;
        }
        if ((Tabs.SelectedItem as TabItem)?.Tag is not SessionTab active)
        {
            return;
        }
        if (item.Resolved.Kind != ArtifactKind.Executable)
        {
            ShowToast("Nur ausführbare Artefakte können in die Toolbar", isError: true);
            return;
        }

        var projectPath = active.Context.Path;
        try
        {
            var existing = SafeLoadProjectConfig(projectPath) ?? new Firepit.Core.ProjectConfig.ProjectConfig();
            var command = new ProjectCommand(
                Name:    item.Resolved.Label,
                Type:    ProjectCommandType.Shell,
                Command: item.Resolved.AbsolutePath,
                Window:  "new");
            var merged = ProjectCommandMutator.Upsert(existing.Commands, command);
            var updated = existing with { Commands = merged };
            _projectConfigStore.Save(projectPath, updated);
            ApplyConfigToOpenTab(projectPath, updated);
            ShowToast($"'{item.Resolved.Label}' ist jetzt ein Toolbar-Button");
            Log.Information("Artifact promoted to command: {Label} in {Project}",
                item.Resolved.Label, active.Context.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not promote artifact {Path}", item.Resolved.AbsolutePath);
            ShowToast($"Konnte nicht übernehmen: {ex.Message}", isError: true);
        }
    }

    private void OnArtifactRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item)
        {
            return;
        }
        if ((Tabs.SelectedItem as TabItem)?.Tag is not SessionTab active)
        {
            return;
        }

        var projectPath = active.Context.Path;
        try
        {
            var existing = _artifactStore.Load(projectPath);
            var (updated, removed) = ArtifactMutator.RemoveByPath(existing, item.Resolved.Path, projectPath);
            if (!removed)
            {
                return;
            }
            _artifactStore.Save(projectPath, updated);
            RenderArtifacts(projectPath, active.Context.Name);
            Log.Information("Artifact unpinned: {Label} in {Project}", item.Resolved.Label, active.Context.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remove artifact {Path}", item.Resolved.AbsolutePath);
            ShowToast($"Konnte nicht entfernen: {ex.Message}", isError: true);
        }
    }
}

/// <summary>
/// View model for one pane row. Holds the resolved artifact plus the brushes
/// and geometry the template binds to — a missing target renders dimmed with
/// its path as the subtitle, so a dead link is visible instead of silently
/// looking fine.
/// </summary>
public sealed class ArtifactItem
{
    private static readonly Brush NormalLabel  = new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD8));
    private static readonly Brush MissingLabel = new SolidColorBrush(Color.FromRgb(0x6B, 0x60, 0x54));
    private static readonly Brush RunnableIcon = new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B));
    private static readonly Brush NormalIcon   = new SolidColorBrush(Color.FromRgb(0xA8, 0x9F, 0x92));
    private static readonly Brush MissingIcon  = new SolidColorBrush(Color.FromRgb(0x6B, 0x60, 0x54));

    public ArtifactItem(ResolvedArtifact resolved)
    {
        Resolved = resolved;
        Icon     = IconFor(resolved.Kind);
    }

    public ResolvedArtifact Resolved { get; }

    public string Label => Resolved.Label;

    public Geometry Icon { get; }

    public Brush IconBrush => !Resolved.Exists
        ? MissingIcon
        : Resolved.Kind == ArtifactKind.Executable ? RunnableIcon : NormalIcon;

    public Brush LabelBrush => Resolved.Exists ? NormalLabel : MissingLabel;

    /// <summary>Note when there is one; otherwise the missing-file warning.</summary>
    public string SubText => !Resolved.Exists
        ? "fehlt — Datei nicht gefunden"
        : Resolved.Note ?? string.Empty;

    public Visibility SubTextVisibility =>
        string.IsNullOrEmpty(SubText) ? Visibility.Collapsed : Visibility.Visible;

    public string ToolTipText => Resolved.Kind == ArtifactKind.Executable
        ? $"{Resolved.AbsolutePath}\nKlick führt dieses Programm aus."
        : Resolved.AbsolutePath;

    private static Geometry IconFor(ArtifactKind kind)
    {
        var key = kind switch
        {
            ArtifactKind.Image      => "IconImage",
            ArtifactKind.Markdown   => "IconFileText",
            ArtifactKind.Text       => "IconFileText",
            ArtifactKind.Document   => "IconFile",
            ArtifactKind.Executable => "IconRunnable",
            ArtifactKind.Archive    => "IconArchive",
            _                       => "IconFile",
        };
        return Application.Current.TryFindResource(key) as Geometry
               ?? Geometry.Parse("M 3,2 L 11,2 L 11,12 L 3,12 Z");
    }
}
