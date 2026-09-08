using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>
/// The usable area of the screen a window is on.
/// </summary>
/// <remarks>
/// <see cref="SystemParameters.WorkArea"/> only ever describes the primary screen, and the
/// accounting team works on two. Laying two windows out by it would fling them onto the other
/// monitor the moment somebody moved the application across.
///
/// Windows is asked instead which monitor the window is on. The answer comes in physical pixels,
/// so it is put through the window's own transform - WPF positions windows in device-independent
/// units, and on a screen at 150% the two differ by half again.
/// </remarks>
internal static class ScreenArea
{
    public static Rect WorkAreaFor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;

        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    /// <summary>Gap between the two windows, and the margin they keep off the screen edge.</summary>
    public const double Gap = 12;

    /// <summary>
    /// Narrowest the right-hand window is worth squeezing to. Below this a page is unreadable and
    /// the two are cascaded instead.
    /// </summary>
    private const double NarrowestPreview = 320;

    /// <summary>
    /// Places two windows side by side, centred on the screen as a pair, or one behind the other
    /// when the screen is too narrow for both.
    /// </summary>
    /// <remarks>
    /// The left one keeps its width - it is a form of fixed-width fields and squeezing it would
    /// cut them off. The right one gives way, because it holds a web page and a page scrolls.
    ///
    /// Below about a thousand points of width there is no arrangement worth having: whatever is
    /// left for the page after a 620-point form is too narrow to read. The two are then cascaded,
    /// offset so both title bars stay visible - the accounting team's screens at 150% scaling come
    /// out that small (1366 x 768 leaves 911 x 470 to work with), and a window pushed half off the
    /// desktop would be worse than an honest overlap.
    ///
    /// Kept apart from the windows so the arithmetic can be checked against screens nobody here
    /// has - it is verified from 2048 x 1104 down to 800 x 600.
    /// </remarks>
    public static (Rect Left, Rect Right) PairSideBySide(Rect area, Size left, Size right)
    {
        var leftWidth = Math.Min(left.Width, area.Width - (2 * Gap));
        var leftHeight = Math.Min(left.Height, area.Height - (2 * Gap));
        var rightHeight = Math.Min(right.Height, area.Height - (2 * Gap));

        var room = area.Width - leftWidth - (3 * Gap);

        if (room < NarrowestPreview)
        {
            // Cascaded: the form where it is, the page offset behind it, both wholly on screen.
            var offset = Math.Min(2 * Gap, Math.Max(0, area.Width - leftWidth - Gap));
            var cascadeWidth = Math.Min(right.Width, area.Width - offset - (2 * Gap));

            return (
                new Rect(area.Left + Gap, area.Top + Gap, leftWidth, leftHeight),
                new Rect(area.Left + Gap + offset,
                         area.Top + Gap + Math.Min(offset, Math.Max(0, area.Height - rightHeight - Gap)),
                         Math.Max(NarrowestPreview, cascadeWidth), rightHeight));
        }

        var rightWidth = Math.Min(right.Width, room);
        var pair = leftWidth + Gap + rightWidth;
        var x = area.Left + Math.Max(Gap, (area.Width - pair) / 2);

        return (
            new Rect(x, area.Top + Math.Max(Gap, (area.Height - leftHeight) / 2), leftWidth, leftHeight),
            new Rect(x + leftWidth + Gap, area.Top + Math.Max(Gap, (area.Height - rightHeight) / 2),
                     rightWidth, rightHeight));
    }

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle Screen;
        public Rectangle Work;
        public int Flags;
    }

    // DllImport rather than LibraryImport: the source-generated version needs unsafe blocks
    // switched on for the whole project, which is a poor trade for two calls with no pointers in
    // them.
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
