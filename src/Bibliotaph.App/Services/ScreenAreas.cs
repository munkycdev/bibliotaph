using System.Runtime.InteropServices;
using Bibliotaph.Core.Layout;

namespace Bibliotaph.App.Services;

/// <summary>
/// The monitors' work areas and a window's rectangle in physical pixels, straight from Windows. WPF's own Left, Top
/// and screen sizes are in device-independent units that change meaning from one monitor to the next when they have
/// different scales, so placements are saved and restored in pixels instead.
/// </summary>
static class ScreenAreas
{
    const uint NoZOrder = 0x0004;
    const uint NoActivate = 0x0010;
    const uint NoOwnerZOrder = 0x0200;
    const int Restore = 9;

    /// <summary>Each monitor's desktop less its taskbar, in pixels.</summary>
    public static IReadOnlyList<ScreenRect> WorkAreas()
    {
        var areas = new List<ScreenRect>();
        // The callback only runs during the call, which keeps the delegate alive until it returns.
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) areas.Add(info.Work.ToScreenRect());
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    /// <summary>The window's rectangle in pixels, or null when it has none.</summary>
    public static ScreenRect? Bounds(IntPtr hwnd) => hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) ? rect.ToScreenRect() : null;

    public static void Move(IntPtr hwnd, ScreenRect bounds) =>
        _ = SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoZOrder | NoActivate | NoOwnerZOrder);

    /// <summary>
    /// Maximized or minimized, as Windows has it now. While a window is being maximized it moves before WPF's
    /// WindowState catches up, so this is what tells a move of the restored window from the maximize itself.
    /// </summary>
    public static bool IsZoomedOrIconic(IntPtr hwnd) => IsZoomed(hwnd) || IsIconic(hwnd);

    /// <summary>Brings back a minimized window as it was before, maximized or not.</summary>
    public static void RestoreIfMinimized(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) _ = ShowWindow(hwnd, Restore);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToScreenRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindow(IntPtr hwnd, int command);
}
