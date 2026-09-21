using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Windows;

/// <summary>
/// Moves a window to a target monitor using Win32 placement APIs, preserving the
/// window's semantic show state (maximized, minimized, or normal).
///
/// All geometry calls execute inside a PMv2 DPI context so that virtual-screen
/// coordinates are interpreted correctly across mixed-DPI monitor setups.
/// The process-wide DPI awareness is never modified.
/// </summary>
public sealed class WindowMoveService : IWindowMoveService
{
    private const int SW_RESTORE      = 9;
    private const int SW_MAXIMIZE     = 3;
    private const int SW_SHOWMINIMIZED = 2;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4 as an integer handle
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new nint(-4);

    private readonly ILogger<WindowMoveService> _logger;

    public WindowMoveService(ILogger<WindowMoveService> logger) => _logger = logger;

    public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo targetMonitor, DisplayTopology topology)
    {
        if (!IsWindow(hwnd))
            return ExecutionResult.Fail(ExecutionStatus.WindowStale, "Window no longer exists");

        // Validate the target monitor is still active in the OS before touching anything.
        var tmi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(targetMonitor.HMonitor, ref tmi))
        {
            _logger.LogWarning("Target monitor HMONITOR={HMon:X} is no longer active", targetMonitor.HMonitor);
            return ExecutionResult.Fail(ExecutionStatus.TopologyStale, "Target monitor is no longer active");
        }

        // Determine window show state before entering the PMv2 context.
        bool isMinimized = IsIconic(hwnd);
        bool isMaximized = !isMinimized && IsZoomed(hwnd);

        // Resolve source monitor work area from topology (by HMONITOR of the window's current monitor).
        var sourceHMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var sourceMonitor = topology.Monitors.FirstOrDefault(m => m.HMonitor == sourceHMon);
        var sourceWorkArea = sourceMonitor?.WorkArea ?? targetMonitor.WorkArea;

        // Find primary work area for workspace ↔ screen coordinate conversion.
        var primaryMonitor = topology.Monitors.FirstOrDefault(m => m.IsPrimary);
        var primaryWorkArea = primaryMonitor?.WorkArea ?? new MonitorBounds(0, 0, 0, 0);

        var prevCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            return isMinimized
                ? MoveMinimized(hwnd, sourceWorkArea, targetMonitor.WorkArea, primaryWorkArea)
                : isMaximized
                    ? MoveMaximized(hwnd, sourceWorkArea, targetMonitor.WorkArea)
                    : MoveNormal(hwnd, sourceWorkArea, targetMonitor.WorkArea);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Window movement failed for HWND={Hwnd:X}", hwnd);
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
        finally
        {
            SetThreadDpiAwarenessContext(prevCtx);
        }
    }

    // ── State-specific movement ──────────────────────────────────────────────────

    private ExecutionResult MoveNormal(nint hwnd, MonitorBounds source, MonitorBounds target)
    {
        var currentRect = new RECT();
        if (!GetWindowRect(hwnd, ref currentRect))
            return ExecutionResult.Fail(ExecutionStatus.WindowStale, "GetWindowRect failed");

        var windowRect = new MonitorBounds(currentRect.left, currentRect.top, currentRect.right, currentRect.bottom);
        var newRect = PlacementPolicy.ComputeNormalPlacement(windowRect, source, target);

        SetWindowPos(hwnd, IntPtr.Zero,
            newRect.Left, newRect.Top, newRect.Width, newRect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);

        _logger.LogInformation("Moved normal window to {Rect}", newRect);
        return ExecutionResult.Ok($"Moved to {target.Left},{target.Top}");
    }

    private ExecutionResult MoveMaximized(nint hwnd, MonitorBounds source, MonitorBounds target)
    {
        // Get the restore rect (rcNormalPosition) before restoring.
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return ExecutionResult.Fail(ExecutionStatus.WindowStale, "GetWindowPlacement failed");

        // Temporarily restore so we can move it via SetWindowPos.
        // SW_RESTORE on a maximized window returns it to its normal (restore) rect.
        ShowWindow(hwnd, SW_RESTORE);

        // The window is now normal-sized at its restore rect on the source monitor.
        // Use PlacementPolicy to compute where it should land on the target.
        var restoreRect = new MonitorBounds(
            wp.rcNormalPosition.left, wp.rcNormalPosition.top,
            wp.rcNormalPosition.right, wp.rcNormalPosition.bottom);
        var newRect = PlacementPolicy.ComputeNormalPlacement(restoreRect, source, target);

        SetWindowPos(hwnd, IntPtr.Zero,
            newRect.Left, newRect.Top, newRect.Width, newRect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);

        // Re-maximize: Windows maximizes to the monitor containing the window, which is now the target.
        ShowWindow(hwnd, SW_MAXIMIZE);

        _logger.LogInformation("Moved maximized window to target work area {WorkArea}", target);
        return ExecutionResult.Ok($"Moved (maximized) to {target.Left},{target.Top}");
    }

    private ExecutionResult MoveMinimized(nint hwnd, MonitorBounds source, MonitorBounds target, MonitorBounds primaryWorkArea)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return ExecutionResult.Fail(ExecutionStatus.WindowStale, "GetWindowPlacement failed");

        // rcNormalPosition is in workspace coordinates (screen coords offset by primary work area origin).
        var workspaceRect = new MonitorBounds(
            wp.rcNormalPosition.left, wp.rcNormalPosition.top,
            wp.rcNormalPosition.right, wp.rcNormalPosition.bottom);

        var screenRect = PlacementPolicy.WorkspaceToScreen(workspaceRect, primaryWorkArea);
        var newScreen  = PlacementPolicy.ComputeNormalPlacement(screenRect, source, target);
        var newWorkspace = PlacementPolicy.ScreenToWorkspace(newScreen, primaryWorkArea);

        wp.rcNormalPosition = new RECT
        {
            left   = newWorkspace.Left,
            top    = newWorkspace.Top,
            right  = newWorkspace.Right,
            bottom = newWorkspace.Bottom
        };
        // Keep showCmd as SW_SHOWMINIMIZED so the window stays minimized.
        wp.showCmd = SW_SHOWMINIMIZED;

        SetWindowPlacement(hwnd, ref wp);

        _logger.LogInformation("Updated minimized window restore rect to target {WorkArea}", target);
        return ExecutionResult.Ok($"Moved (minimized restore rect) to {target.Left},{target.Top}");
    }

    // ── P/Invokes ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool   IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool   IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool   IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool   ShowWindow(nint hwnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool   GetWindowRect(nint hwnd, ref RECT lpRect);
    [DllImport("user32.dll")] private static extern bool   GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool   SetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool   SetWindowPos(nint hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(nint hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll")] private static extern nint   SetThreadDpiAwarenessContext(nint dpiContext);

    // ── Win32 structs ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint  length;
        public uint  flags;
        public int   showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT  rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
