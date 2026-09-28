using System.Runtime.InteropServices;

namespace VoiceOS.Eval.Live;

/// <summary>Eval-only, read-only Win32 window geometry. Requires a per-monitor DPI-aware process
/// (the live runner sets PerMonitorV2, matching the tray app) so values are physical pixels.</summary>
internal static class WindowGeometryProbe
{
    public static (WindowRect? Rect, bool Minimized, bool Maximized, nint Monitor) Read(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return (null, false, false, 0);
        WindowRect? rect = null;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) == 0
            || GetWindowRect(hwnd, out frame))
            rect = new WindowRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        return (rect, IsIconic(hwnd), IsZoomed(hwnd), MonitorFromWindow(hwnd, MonitorDefaultToNearest));
    }

    private const int DwmwaExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
