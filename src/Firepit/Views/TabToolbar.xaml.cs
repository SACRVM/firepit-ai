using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Firepit.Core.ProjectConfig;
using Firepit.Core.QuickLinks;
using Firepit.Resources;

namespace Firepit.Views;

public partial class TabToolbar : UserControl
{
    public TabToolbar()
    {
        InitializeComponent();
        // Catch every Button-Click that bubbles up through the toolbar (static
        // toolbar buttons + dynamic Quick-links + Commands) and ask the host
        // to return focus to the terminal. Routed-event delivery is synchronous,
        // so for handlers that open a modal (Inbox), this fires only after the
        // modal closes — exactly the moment we want the terminal to take focus.
        // Context-menu items live in a separate visual tree and don't bubble
        // here; those re-raise the event themselves.
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => RequestFocusReturn()));
    }

    /// <summary>Raised after any toolbar action so the host can return focus
    /// to the terminal — buttons shouldn't keep the keyboard focus.</summary>
    public event EventHandler? FocusReturnRequested;

    private void RequestFocusReturn() => FocusReturnRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? RekindleRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler? ExplorerRequested;
    /// <summary>Raised to open an external shell. The bool argument is true to
    /// launch elevated (run as administrator).</summary>
    public event EventHandler<bool>? ShellRequested;
    public event EventHandler? EditorRequested;
    public event EventHandler? ConfigureRequested;
    /// <summary>Raised when the user clicks the always-visible Inbox toolbar
    /// button. SessionTab owns the modal-confirm + PTY-paste flow; the toolbar
    /// only signals intent.</summary>
    public event EventHandler? InboxRequested;
    public event EventHandler<ResolvedQuickLink>? QuickLinkClicked;
    public event EventHandler<ProjectCommand>? CommandClicked;
    /// <summary>Right-click → Stop on a long-running command's toolbar button.
    /// Only raised when the command is currently tracked-alive.</summary>
    public event EventHandler<ProjectCommand>? CommandStopRequested;

    /// <summary>
    /// Update the Inbox toolbar button. Count==0 → label collapses to "Inbox"
    /// and the button greys out (still visible — discoverable). Count&gt;0 →
    /// label becomes "Inbox (N)" and the button becomes clickable.
    /// </summary>
    public void SetInboxCount(int count)
    {
        if (count <= 0)
        {
            InboxLabel.Text = "Inbox";
            InboxButton.IsEnabled = false;
            InboxButton.ToolTip = "Inbox is empty";
        }
        else
        {
            InboxLabel.Text = $"Inbox ({count})";
            InboxButton.IsEnabled = true;
            InboxButton.ToolTip = count == 1
                ? "Process the pending inbox message with Claude"
                : $"Process {count} pending inbox messages with Claude";
        }
    }

    private void OnInboxClick(object sender, RoutedEventArgs e) => InboxRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Re-render the per-project command buttons. <paramref name="isRunning"/>
    /// (Phase B) decides which buttons gain the live-dot prefix + a "Stop"
    /// context-menu item. Pass <c>_ =&gt; false</c> if no lifecycle tracking
    /// is wired up.
    /// </summary>
    public void SetCommands(IReadOnlyList<ProjectCommand> commands, Func<ProjectCommand, bool>? isRunning = null)
    {
        var query = isRunning ?? (_ => false);
        var style = (Style)FindResource("ToolbarIconButton");
        var buttons = new List<Button>();
        // CommandGrouping decides what collapses (opt-in groups of 2+) vs. what
        // stays a standalone button — so a project that doesn't use groups (or a
        // genuine multi-command project) renders exactly as it did before.
        foreach (var unit in CommandGrouping.Plan(commands))
        {
            buttons.Add(unit.IsGroup
                ? BuildGroupButton(unit.GroupLabel!, unit.GroupMembers!, query, style)
                : BuildSingleCommandButton(unit.Single!, query(unit.Single!), style));
        }
        Commands.ItemsSource = buttons;
    }

    private Button BuildSingleCommandButton(ProjectCommand cmd, bool running, Style style)
    {
        var button = new Button
        {
            Content = BuildCommandContent(cmd, running),
            Style = style,
            Tag = cmd,
            ToolTip = BuildCommandTooltip(cmd, running),
        };
        button.Click += OnCommandClick;

        // Stop only makes sense once tracked-alive. Shows a single-item context
        // menu so the right-click affordance is discoverable even for
        // non-running commands (they get a disabled "Not running" entry — keeps
        // menu positioning consistent). Elevated children can't be killed from
        // the non-elevated parent — the menu surfaces that explicitly instead
        // of silently failing.
        var menu = new ContextMenu();
        var elevatedAndRunning = running && cmd.Elevated == true;
        var stopItem = new MenuItem
        {
            Header = elevatedAndRunning ? "Stop (elevated — close its window manually)" : "Stop",
            IsEnabled = running && !elevatedAndRunning,
            Tag = cmd,
        };
        stopItem.Click += OnStopMenuClick;
        menu.Items.Add(stopItem);
        button.ContextMenu = menu;
        return button;
    }

    /// <summary>
    /// A collapsed multi-target group: one toolbar button labelled with the
    /// group name that drops down its member commands. Left-click opens the
    /// dropdown (it's a menu, not a split button). A running member shows a dot
    /// on both the button and its menu entry, plus a Stop entry.
    /// </summary>
    private Button BuildGroupButton(
        string group, IReadOnlyList<ProjectCommand> members, Func<ProjectCommand, bool> query, Style style)
    {
        var anyRunning = members.Any(query);
        var button = new Button
        {
            Content = BuildGroupContent(group, members, anyRunning),
            Style = style,
            ToolTip = "Run: " + string.Join(", ", members.Select(m => m.Name)),
        };

        var menu = new ContextMenu();
        foreach (var m in members)
        {
            var running = query(m);
            var item = new MenuItem
            {
                Header = running ? "● " + m.Name : m.Name,
                Tag = m,
            };
            item.Click += OnGroupMemberClick;
            menu.Items.Add(item);

            if (running)
            {
                var stop = new MenuItem
                {
                    Header = m.Elevated == true
                        ? $"Stop {m.Name} (elevated — close its window manually)"
                        : $"Stop {m.Name}",
                    IsEnabled = m.Elevated != true,
                    Tag = m,
                };
                stop.Click += OnStopMenuClick;
                menu.Items.Add(stop);
            }
        }
        button.ContextMenu = menu;
        button.Click += OnGroupButtonClick;
        return button;
    }

    private void OnGroupButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        // Don't let this bubble to the toolbar's global focus-return handler —
        // handing focus to the terminal hwnd while the dropdown is opening would
        // dismiss the popup. A picked member re-raises the focus return itself.
        e.Handled = true;
    }

    private void OnGroupMemberClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProjectCommand cmd })
        {
            CommandClicked?.Invoke(this, cmd);
        }
        RequestFocusReturn();
    }

    private void OnCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectCommand cmd })
        {
            CommandClicked?.Invoke(this, cmd);
        }
    }

    private void OnStopMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProjectCommand cmd })
        {
            CommandStopRequested?.Invoke(this, cmd);
        }
        RequestFocusReturn();
    }

    private static string BuildCommandTooltip(ProjectCommand cmd, bool running = false)
    {
        var basis = cmd.Type switch
        {
            ProjectCommandType.Shell         => $"Shell: {cmd.Command} {string.Join(' ', cmd.Args ?? [])}",
            ProjectCommandType.ClaudePrompt  => $"Send to Claude: \"{Truncate(cmd.Prompt, 80)}\"",
            ProjectCommandType.Url           => $"Open: {cmd.Url}",
            _                                => cmd.Name,
        };
        if (running)
        {
            return basis + "\n● Running — right-click to stop"
                + (CommandRunner.TryParseReuse(cmd.Window, out _) ? " (click focuses the window)" : string.Empty);
        }
        return basis;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";

    private static StackPanel BuildCommandContent(ProjectCommand cmd, bool running = false)
    {
        var (geometry, mode) = IconResolver.Resolve(cmd.Icon, fallbackName: cmd.Name);

        var path = new Path
        {
            Data = geometry,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        var foregroundBinding = new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        };
        if (mode == Firepit.Resources.IconMode.Fill)
        {
            path.SetBinding(Shape.FillProperty, foregroundBinding);
        }
        else
        {
            path.SetBinding(Shape.StrokeProperty, foregroundBinding);
            path.StrokeThickness = 1.2;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.Stretch = Stretch.None;
        }

        var text = new TextBlock
        {
            Text = cmd.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        // Live indicator for tracked long-running / reuse commands. A small
        // burning-warm dot tells the eye at a glance that the watcher is up,
        // without stealing the button's icon slot.
        if (running)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B)), // burning warm
            };
            panel.Children.Add(dot);
        }

        panel.Children.Add(path);
        panel.Children.Add(text);
        return panel;
    }

    private static StackPanel BuildGroupContent(string group, IReadOnlyList<ProjectCommand> members, bool running)
    {
        // Borrow the first member's icon as the group glyph; fall back to a
        // name-derived icon. A trailing caret marks it as a dropdown.
        var iconHint = members.Select(m => m.Icon).FirstOrDefault(i => !string.IsNullOrEmpty(i));
        var (geometry, mode) = IconResolver.Resolve(iconHint, fallbackName: group);

        var path = new Path
        {
            Data = geometry,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        var foregroundBinding = new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        };
        if (mode == Firepit.Resources.IconMode.Fill)
        {
            path.SetBinding(Shape.FillProperty, foregroundBinding);
        }
        else
        {
            path.SetBinding(Shape.StrokeProperty, foregroundBinding);
            path.StrokeThickness = 1.2;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.Stretch = Stretch.None;
        }

        var text = new TextBlock
        {
            Text = group + "  ▾", // group name + ▾
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (running)
        {
            panel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xC9, 0x7B)), // burning warm
            });
        }
        panel.Children.Add(path);
        panel.Children.Add(text);
        return panel;
    }

    public void SetQuickLinks(IReadOnlyList<ResolvedQuickLink> links)
    {
        var buttons = new List<Button>(links.Count);
        var style = (Style)FindResource("ToolbarIconButton");
        foreach (var link in links)
        {
            var button = new Button
            {
                Content = BuildQuickLinkContent(link),
                Style = style,
                Tag = link,
                IsEnabled = link.Available,
                ToolTip = link.Available
                    ? link.Url
                    : $"{link.Name} — {link.UnavailableReason}",
            };
            button.Click += OnQuickLinkClick;
            buttons.Add(button);
        }
        QuickLinks.ItemsSource = buttons;
    }

    private static StackPanel BuildQuickLinkContent(ResolvedQuickLink link)
    {
        // Single resolver — same path-data + named-icon fallback shared with
        // commands. Quick-links may pass either a named hint or pasted SVG
        // path data.
        var (geometry, mode) = Firepit.Resources.IconResolver.Resolve(link.Icon, fallbackName: link.Name);

        var path = new Path
        {
            Data = geometry,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };

        var foregroundBinding = new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        };

        if (mode == Firepit.Resources.IconMode.Fill)
        {
            path.SetBinding(Shape.FillProperty, foregroundBinding);
        }
        else
        {
            path.SetBinding(Shape.StrokeProperty, foregroundBinding);
            path.StrokeThickness = 1.2;
            path.StrokeLineJoin = PenLineJoin.Round;
            // Stroked glyphs are designed at native size; uniform-stretching them
            // would scale the stroke too. Keep them at natural geometry coords.
            path.Stretch = Stretch.None;
        }

        var text = new TextBlock
        {
            Text = link.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(path);
        panel.Children.Add(text);
        return panel;
    }

    private void OnRekindleClick(object sender, RoutedEventArgs e)  => RekindleRequested?.Invoke(this, EventArgs.Empty);
    private void OnResumeClick(object sender, RoutedEventArgs e)    => ResumeRequested?.Invoke(this, EventArgs.Empty);
    private void OnExplorerClick(object sender, RoutedEventArgs e)  => ExplorerRequested?.Invoke(this, EventArgs.Empty);
    private void OnEditorClick(object sender, RoutedEventArgs e)    => EditorRequested?.Invoke(this, EventArgs.Empty);
    private void OnConfigureClick(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(this, EventArgs.Empty);

    // Left-click opens a normal shell; Shift+Click opens it elevated — the
    // modifier state is read at click time so it's reliable even though the
    // WebView2 owns keyboard focus. The right-click context menu exposes the
    // same two choices for discoverability.
    private void OnShellClick(object sender, RoutedEventArgs e)
    {
        var elevated = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        ShellRequested?.Invoke(this, elevated);
    }

    private void OnShellOpenMenuClick(object sender, RoutedEventArgs e)
    {
        ShellRequested?.Invoke(this, false);
        RequestFocusReturn();
    }

    private void OnShellAdminMenuClick(object sender, RoutedEventArgs e)
    {
        ShellRequested?.Invoke(this, true);
        RequestFocusReturn();
    }

    private void OnQuickLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ResolvedQuickLink link })
        {
            QuickLinkClicked?.Invoke(this, link);
        }
    }
}
