using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SysProcess = System.Diagnostics.Process;

namespace Firepit.Native;

/// <summary>
/// Records who takes the keyboard away.
///
/// Focus theft while typing is intermittent and leaves no trace behind: by the
/// time the user notices the keystrokes are going nowhere, whatever grabbed
/// focus has already been forgotten. Guessing from the code doesn't converge
/// either — the plausible suspects (an OS-level foreground grab from another
/// app, one of our own popups, a WebView2 host that took WPF focus without the
/// terminal ever receiving it) all look identical from the user's side.
///
/// So we log the moment itself. Two signals, both rare enough to sit at
/// Information without drowning the file:
/// <list type="bullet">
///   <item>the window losing OS activation, plus the process that took the
///   foreground — names an external thief outright;</item>
///   <item>WPF keyboard focus going to <c>null</c> — nothing is focused, which
///   is exactly the "typing into the void" symptom, and points inward.</item>
/// </list>
/// Ordinary element-to-element focus moves are Debug and never reach the file.
/// </summary>
internal static class FocusDiagnostics
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Deactivated += (_, _) =>
        {
            var hwnd = GetForegroundWindow();
            Log.Information(
                "Focus diagnostics: window deactivated, foreground is now {Foreground} (hwnd 0x{Hwnd:X})",
                DescribeForeground(hwnd), hwnd.ToInt64());
        };

        window.Activated += (_, _) =>
            Log.Information("Focus diagnostics: window reactivated");

        // Bubbling, so this sees focus changes anywhere below the window.
        window.AddHandler(
            UIElement.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus),
            handledEventsToo: true);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is null)
        {
            // Nobody holds the keyboard now — every subsequent keystroke is
            // dropped on the floor until something takes focus back.
            Log.Information(
                "Focus diagnostics: keyboard focus went to nothing (was {Old}) — keystrokes will be lost",
                Describe(e.OldFocus));
            return;
        }
        Log.Debug(
            "Focus diagnostics: keyboard focus {Old} -> {New}",
            Describe(e.OldFocus), Describe(e.NewFocus));
    }

    private static string Describe(IInputElement? element) => element switch
    {
        null => "(none)",
        FrameworkElement { Name.Length: > 0 } fe => $"{fe.GetType().Name}#{fe.Name}",
        _ => element.GetType().Name,
    };

    private static string DescribeForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(no foreground window)";
        }
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return "(unknown process)";
            }
            using var proc = SysProcess.GetProcessById(pid);
            var title = proc.MainWindowTitle;
            return pid == Environment.ProcessId
                ? $"Firepit itself ('{title}')"
                : $"{proc.ProcessName} (pid {pid}, '{title}')";
        }
        catch (Exception)
        {
            // Process gone, or access denied on an elevated window. The hwnd
            // logged by the caller is still a usable clue.
            return "(process not identifiable)";
        }
    }
}
