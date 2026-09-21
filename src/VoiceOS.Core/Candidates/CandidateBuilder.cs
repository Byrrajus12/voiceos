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
    // Shared across snapshots; evicts entries when their process exits.
    internal static ProcessMetadataCache _processCache = new();
    public static IReadOnlyList<AppCandidate> GetInstalledApps(IAppCatalog catalog)
        => catalog.GetAll()
                  .Select(e => new AppCandidate(e.Id, e.DisplayName, e.ProcessName, e.AppUserModelId))
                  .ToList();

    public static IReadOnlyList<WindowCandidate> GetOpenWindows()
        => GetOpenWindowsWithTimings(out _);

    /// <summary>
    /// Enumerates open windows and returns per-phase timing breakdowns.
    /// enumerateMs = total wall time minus per-window process and AUMID lookup time.
    /// processMs   = cumulative time spent in process name lookups across all visible titled windows.
    /// aumidMs     = cumulative time spent in WindowPropertyStore.GetPerWindowAumid across same.
    /// cacheHits/cacheMisses = per-snapshot hit ratio for the PID metadata cache.
    /// </summary>
    public static IReadOnlyList<WindowCandidate> GetOpenWindowsWithTimings(out WindowEnumerationTimings timings)
    {
        var windows = new List<WindowCandidate>();
        var foreground = GetForegroundWindow();
        var titleBuf = new StringBuilder(512);
        long processMs = 0, aumidMs = 0;
        int cacheHits = 0, cacheMisses = 0;
        var totalSw = Stopwatch.StartNew();
        var sw = new Stopwatch();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            titleBuf.Clear();
            int len = GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
            if (len == 0) return true;

            string title = titleBuf.ToString();

            sw.Restart();
            string processName = GetProcessName(hWnd, out bool hit);
            sw.Stop();
            processMs += sw.ElapsedMilliseconds;
            if (hit) cacheHits++; else cacheMisses++;

            sw.Restart();
            string? aumid = WindowPropertyStore.GetPerWindowAumid(hWnd);
            sw.Stop();
            aumidMs += sw.ElapsedMilliseconds;

            windows.Add(new WindowCandidate(
                Id: $"w{windows.Count}",
                ProcessName: processName,
                Title: title,
                IsForeground: hWnd == foreground,
                Hwnd: hWnd,
                AppUserModelId: aumid,
                ExecutablePath: null));

            return true;
        }, IntPtr.Zero);

        totalSw.Stop();
        timings = new WindowEnumerationTimings(
            EnumerateMs: totalSw.ElapsedMilliseconds - processMs - aumidMs,
            ProcessMs: processMs,
            AumidMs: aumidMs,
            CacheHits: cacheHits,
            CacheMisses: cacheMisses);
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

    private static string GetProcessName(IntPtr hWnd, out bool cacheHit)
    {
        cacheHit = false;
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return string.Empty;
            return _processCache.GetProcessName((int)pid, out cacheHit);
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

public record WindowEnumerationTimings(
    long EnumerateMs,
    long ProcessMs,
    long AumidMs,
    int CacheHits = 0,
    int CacheMisses = 0);
