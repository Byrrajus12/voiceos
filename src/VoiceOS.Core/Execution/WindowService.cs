using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Executes window operations using Win32 APIs.
/// Named-target operations resolve the planning-time HWND from the snapshot.
/// Stale HWNDs return WindowStale — no silent fallback to foreground.
/// Current-window (null target) operations act on the foreground window at execution time.
/// </summary>
public sealed class WindowService : IWindowService
{
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const uint WM_CLOSE = 0x0010;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;

    private readonly ILogger<WindowService> _logger;

    public WindowService(ILogger<WindowService> logger) => _logger = logger;

    public ExecutionResult Focus(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (string.IsNullOrEmpty(windowCandidateId))
            return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No window candidate ID");

        if (!TryResolveHwnd(windowCandidateId, snapshot, out var hwnd, out var error))
            return error;

        try
        {
            BringToForeground(hwnd);
            var title = snapshot.FirstOrDefault(w => w.Id == windowCandidateId)?.Title ?? windowCandidateId;
            _logger.LogInformation("Focused window: {Title}", title);
            return ExecutionResult.Ok($"Focused: {title}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to focus window {Id}", windowCandidateId);
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
    }

    public ExecutionResult Close(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (!TryResolveHwnd(windowCandidateId, snapshot, out var hwnd, out var error))
            return error;
        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _logger.LogInformation("Close sent to {Target}", windowCandidateId ?? "foreground window");
        return ExecutionResult.Ok("Close sent");
    }

    public ExecutionResult Maximize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (!TryResolveHwnd(windowCandidateId, snapshot, out var hwnd, out var error))
            return error;
        ShowWindow(hwnd, SW_MAXIMIZE);
        _logger.LogInformation("Maximized {Target}", windowCandidateId ?? "foreground window");
        return ExecutionResult.Ok("Maximized");
    }

    public ExecutionResult Minimize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (!TryResolveHwnd(windowCandidateId, snapshot, out var hwnd, out var error))
            return error;
        ShowWindow(hwnd, SW_MINIMIZE);
        _logger.LogInformation("Minimized {Target}", windowCandidateId ?? "foreground window");
        return ExecutionResult.Ok("Minimized");
    }

    public ExecutionResult Snap(SnapDirection direction, string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (!TryResolveHwnd(windowCandidateId, snapshot, out var hwnd, out var error))
            return error;

        // Win+Arrow snaps the foreground window, so bring the named window forward first.
        if (windowCandidateId != null)
        {
            try { BringToForeground(hwnd); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to focus window before snap");
                return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
            }
        }

        ushort arrowKey = direction == SnapDirection.Left ? VK_LEFT : VK_RIGHT;
        try
        {
            SendKeyCombo(VK_LWIN, arrowKey);
            _logger.LogInformation("Snapped {Direction} ({Target})", direction, windowCandidateId ?? "foreground");
            return ExecutionResult.Ok($"Snapped {direction}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snap failed");
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
    }

    /// <summary>
    /// Resolves an HWND from the snapshot (named target) or from GetForegroundWindow (null target).
    /// Returns false and sets <paramref name="error"/> if the window cannot be resolved.
    /// A named target with a stale HWND returns WindowStale — never falls back to foreground.
    /// </summary>
    private bool TryResolveHwnd(
        string? windowCandidateId,
        IReadOnlyList<WindowCandidate> snapshot,
        out IntPtr hwnd,
        out ExecutionResult error)
    {
        if (windowCandidateId is null)
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                error = ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No foreground window");
                return false;
            }
            error = default!;
            return true;
        }

        var candidate = snapshot.FirstOrDefault(w => w.Id == windowCandidateId);
        if (candidate is null)
        {
            hwnd = IntPtr.Zero;
            error = ExecutionResult.Fail(ExecutionStatus.WindowNotFound, $"Window '{windowCandidateId}' not in snapshot");
            return false;
        }

        if (candidate.Hwnd == 0 || !IsWindow(candidate.Hwnd))
        {
            _logger.LogWarning("HWND for '{Title}' is stale or zero", candidate.Title);
            hwnd = IntPtr.Zero;
            error = ExecutionResult.Fail(ExecutionStatus.WindowStale, $"Window '{candidate.Title}' no longer exists");
            return false;
        }

        hwnd = candidate.Hwnd;
        error = default!;
        return true;
    }

    private static void BringToForeground(IntPtr hwnd)
    {
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint myThread = GetCurrentThreadId();

        bool attached = fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
    }

    private static void SendKeyCombo(ushort modifier, ushort key)
    {
        var inputs = new INPUT[4];
        inputs[0] = KeyInput(modifier, 0);
        inputs[1] = KeyInput(key, 0);
        inputs[2] = KeyInput(key, KEYEVENTF_KEYUP);
        inputs[3] = KeyInput(modifier, KEYEVENTF_KEYUP);
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
