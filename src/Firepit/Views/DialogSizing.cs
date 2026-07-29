using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Firepit.Views;

/// <summary>
/// Keeps dialogs inside the monitor they open on. Firepit's dialogs size
/// themselves to their content, so a long body (release notes, an exception
/// message) grew the window past the bottom of the screen and pushed the
/// button row out of reach. Applying this caps the window against the owner's
/// work area — the body scrolls instead of the window growing — and scales the
/// design width with the UI font setting so text isn't squeezed into a narrow
/// column at larger fonts.
/// </summary>
internal static partial class DialogSizing
{
    /// <summary>Caption height the dialog layouts were designed against.</summary>
    private const double BaseCaptionHeight = 32.0;

    /// <summary>Share of the work area a dialog may occupy at most.</summary>
    private const double MaxWidthFraction = 0.90;
    private const double MaxHeightFraction = 0.85;

    /// <summary>
    /// UI font scale, 1.0 at the default font size — derived from the same
    /// resource <c>App.ApplyFontResources</c> scales.
    /// </summary>
    public static double FontScale(FrameworkElement element) =>
        element.TryFindResource("DialogCaptionPixelHeight") is double captionHeight && captionHeight > 0
            ? captionHeight / BaseCaptionHeight
            : 1.0;

    /// <summary>
    /// Size <paramref name="dialog"/> from <paramref name="designWidth"/> scaled
    /// by the font setting, and clamp it to the screen. Call after
    /// <see cref="Window.Owner"/> is assigned and before showing.
    /// </summary>
    public static void Apply(Window dialog, double designWidth)
    {
        var work = WorkAreaDips(dialog.Owner);
        ClampToScreen(dialog, work);
        dialog.Width = Math.Min(designWidth * FontScale(dialog), work.Width * MaxWidthFraction);
    }

    /// <summary>
    /// Cap a window that manages its own width against the work area, and keep
    /// it fully on-screen once shown.
    /// </summary>
    public static void ClampToScreen(Window dialog) => ClampToScreen(dialog, WorkAreaDips(dialog.Owner));

    private static void ClampToScreen(Window dialog, Size work)
    {
        dialog.MaxWidth = work.Width * MaxWidthFraction;
        dialog.MaxHeight = work.Height * MaxHeightFraction;
        // CenterOwner can still place a capped window partly off-screen when the
        // owner sits near a monitor edge — correct the final placement.
        dialog.ContentRendered -= OnContentRendered;
        dialog.ContentRendered += OnContentRendered;
    }

    private static void OnContentRendered(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            NudgeIntoWorkArea(window);
        }
    }

    /// <summary>
    /// Work area of the monitor <paramref name="reference"/> lives on, in DIPs.
    /// Falls back to the primary monitor when there is no shown reference window.
    /// </summary>
    private static Size WorkAreaDips(Window? reference)
    {
        if (reference is not null
            && PresentationSource.FromVisual(reference) is HwndSource source
            && TryGetWorkArea(source.Handle, out var work))
        {
            var dpi = VisualTreeHelper.GetDpi(reference);
            return new Size(
                (work.Right - work.Left) / dpi.DpiScaleX,
                (work.Bottom - work.Top) / dpi.DpiScaleY);
        }
        var primary = SystemParameters.WorkArea;
        return new Size(primary.Width, primary.Height);
    }

    /// <summary>
    /// Move the window back inside its monitor's work area. Done in device
    /// pixels via Win32 so it stays correct across mixed-DPI monitors.
    /// </summary>
    private static void NudgeIntoWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero
            || !GetWindowRect(hwnd, out var bounds)
            || !TryGetWorkArea(hwnd, out var work))
        {
            return;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var x = Math.Max(work.Left, Math.Min(bounds.Left, work.Right - width));
        var y = Math.Max(work.Top, Math.Min(bounds.Top, work.Bottom - height));
        if (x == bounds.Left && y == bounds.Top)
        {
            return;
        }
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static bool TryGetWorkArea(IntPtr hwnd, out NativeRect work)
    {
        work = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }
        work = info.Work;
        return true;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
