using System.Runtime.InteropServices;

namespace VoiceOS.Core.Dictation;

public sealed class WindowsForegroundWindowService : IForegroundWindowService
{
    public TextInsertionTarget Capture()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return default;
        GetWindowThreadProcessId(hwnd, out var processId);
        return new(hwnd, processId);
    }

    public bool IsCurrent(TextInsertionTarget target)
    {
        if (!target.IsValid || !IsWindow(target.WindowHandle))
            return false;
        var hwnd = GetForegroundWindow();
        if (hwnd != target.WindowHandle)
            return false;
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == target.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);
}
