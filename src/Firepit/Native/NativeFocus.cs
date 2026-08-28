using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Firepit.Native;

/// <summary>
/// Thin Win32 wrapper for OS-level keyboard focus. WPF's Focus / Keyboard.Focus
/// only move the *logical* focus inside a HWND — they can't pull the OS focus
/// out of a sibling HWND that lives inside the same window. The project-picker
/// popup runs into this: WebView2 (the embedded terminal) keeps the OS focus
/// even after the popup opens, so typed characters end up in the running
/// Claude session instead of the search box.
///
/// The fix is to call <see cref="SetFocus"/> on the popup's HWND (popups with
/// AllowsTransparency="True" are real HWNDs, reachable via HwndSource) so the
/// OS routes WM_KEYDOWN there, and then set WPF Keyboard.Focus on the actual
/// TextBox inside.
/// </summary>
internal static class NativeFocus
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// True when the window the user is currently working in belongs to this
    /// process — i.e. Firepit is the app in front, whichever of its windows,
    /// popups or dialogs happens to hold it.
    /// </summary>
    /// <remarks>
    /// The question every automatic focus call has to ask first. Win32
    /// <c>SetFocus</c> does not only move the caret: if the top-level window
    /// owning the target is not active, the system activates it. So handing
    /// focus to the terminal from a background window drags the whole app in
    /// front of whatever the user moved on to. Asking this first is the
    /// difference between "restore the keyboard where the user already is"
    /// and "interrupt them".
    /// </remarks>
    public static bool AppIsInForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            // No foreground window at all (a lock screen, a switch in
            // progress). Not ours, and guessing "yes" here is what makes an
            // app pop up over a locked desktop.
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Hand OS keyboard focus to <paramref name="window"/>'s own HWND.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="MoveOsFocusTo"/>, and the reason it has
    /// to exist: a popup with AllowsTransparency is a real top-level HWND, and
    /// once we have pushed OS focus into it, closing it destroys the window
    /// that holds the focus. Windows then picks the successor itself, and its
    /// pick is regularly another application — the app drops out of the
    /// foreground for no reason the user can see. Giving the focus back before
    /// the HWND goes away leaves nothing for the system to guess about.
    /// </remarks>
    public static void ReturnOsFocusTo(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetFocus(hwnd);
        }
    }

    /// <summary>
    /// Move OS keyboard focus to the HWND that hosts <paramref name="visual"/>.
    /// No-op (returns false) if the visual isn't yet attached to an HwndSource
    /// — caller should re-try on a later dispatcher tick if so.
    /// </summary>
    public static bool MoveOsFocusTo(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource src) return false;
        SetFocus(src.Handle);
        return true;
    }
}
