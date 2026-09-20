using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceOS.Core.Candidates;

public static class CandidateBuilder
{
    private static readonly AppCandidate[] DevCatalog =
    [
        new AppCandidate("chrome", "Google Chrome", "chrome"),
        new AppCandidate("firefox", "Firefox", "firefox"),
        new AppCandidate("edge", "Microsoft Edge", "msedge"),
        new AppCandidate("vscode", "Visual Studio Code", "Code"),
        new AppCandidate("vs", "Visual Studio", "devenv"),
        new AppCandidate("terminal", "Windows Terminal", "WindowsTerminal"),
        new AppCandidate("cmd", "Command Prompt", "cmd"),
        new AppCandidate("powershell", "PowerShell", "pwsh"),
        new AppCandidate("explorer", "File Explorer", "explorer"),
        new AppCandidate("notepad", "Notepad", "notepad"),
        new AppCandidate("spotify", "Spotify", "Spotify"),
        new AppCandidate("slack", "Slack", "slack"),
        new AppCandidate("discord", "Discord", "Discord"),
        new AppCandidate("teams", "Microsoft Teams", "ms-teams"),
        new AppCandidate("outlook", "Outlook", "olk"),
        new AppCandidate("obs", "OBS Studio", "obs64"),
        new AppCandidate("obsidian", "Obsidian", "Obsidian"),
        new AppCandidate("telegram", "Telegram", "Telegram"),
        new AppCandidate("whatsapp", "WhatsApp", "WhatsApp"),
        new AppCandidate("calc", "Calculator", "CalculatorApp"),
    ];

    public static IReadOnlyList<AppCandidate> GetInstalledApps() => DevCatalog;

    public static IReadOnlyList<WindowCandidate> GetOpenWindows()
    {
        var windows = new List<WindowCandidate>();

        IntPtr foreground = GetForegroundWindow();
        var titleBuf = new StringBuilder(512);

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            titleBuf.Clear();
            int len = GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
            if (len == 0) return true;

            string title = titleBuf.ToString();
            string processName = GetProcessName(hWnd);

            windows.Add(new WindowCandidate(
                Id: $"w{windows.Count}",
                ProcessName: processName,
                Title: title,
                IsForeground: hWnd == foreground));

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

    private static string GetProcessName(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return string.Empty;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
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
