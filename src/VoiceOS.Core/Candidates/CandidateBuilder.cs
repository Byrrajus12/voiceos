using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Windows;

namespace VoiceOS.Core.Candidates;

/// <summary>
/// Builds candidate snapshots for the planning layer.
/// AppCandidate lists are derived from the IAppCatalog; window snapshots include HWNDs and
/// per-window AUMIDs for accurate identity matching in the execution layer.
/// </summary>
public static class CandidateBuilder
{
    public static IReadOnlyList<AppCandidate> GetInstalledApps(IAppCatalog catalog)
        => catalog.GetAll()
                  .Select(e => new AppCandidate(e.Id, e.DisplayName, e.ProcessName, e.AppUserModelId))
                  .ToList();

    public static IReadOnlyList<WindowCandidate> GetOpenWindows()
    {
        var windows = new List<WindowCandidate>();
        var foreground = GetForegroundWindow();
        var titleBuf = new StringBuilder(512);

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            titleBuf.Clear();
            int len = GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
            if (len == 0) return true;

            string title = titleBuf.ToString();
            var (processName, executablePath) = GetProcessInfo(hWnd);
            string? aumid = WindowPropertyStore.GetPerWindowAumid(hWnd);

            windows.Add(new WindowCandidate(
                Id: $"w{windows.Count}",
                ProcessName: processName,
                Title: title,
                IsForeground: hWnd == foreground,
                Hwnd: hWnd,
                AppUserModelId: aumid,
                ExecutablePath: executablePath));

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static string GetForegroundAppName()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return string.Empty;

        var buf = new StringBuilder(512);
        GetWindowText(hWnd, buf, buf.Capacity);
        return buf.ToString();
    }

    private static (string processName, string? executablePath) GetProcessInfo(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return (string.Empty, null);
            using var proc = Process.GetProcessById((int)pid);
            string? exePath = null;
            try { exePath = proc.MainModule?.FileName; } catch { }
            return (proc.ProcessName, exePath);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
