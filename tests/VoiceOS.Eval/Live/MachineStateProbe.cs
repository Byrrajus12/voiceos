using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;

namespace VoiceOS.Eval.Live;

/// <summary>Captures a plain-data snapshot of visible machine state via the live product's
/// already-built services. Real Windows/Chrome calls — not unit tested.</summary>
public sealed class MachineStateProbe(VoiceOS.VoiceOSProduct product)
{
    public async Task<StateSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var topology = product.Topology.CaptureTopology().Monitors;
        var monitors = topology.Select((m, i) => new MonitorArea(i, m.IsPrimary,
            new WindowRect(m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Width, m.WorkArea.Height))).ToArray();

        // All windows (no cap): absence and "new window" checks need the complete set.
        var windows = CandidateBuilder.GetOpenWindows();
        var windowInfos = windows.Select(w =>
        {
            var (rect, minimized, maximized, monitor) = WindowGeometryProbe.Read(w.Hwnd);
            var monitorIndex = topology.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.HMonitor == monitor && monitor != 0);
            return new WindowInfo(w.ProcessName, w.Title, w.IsForeground, w.Hwnd, rect, minimized, maximized,
                monitorIndex.m is null ? null : monitorIndex.i);
        }).ToArray();
        var foreground = SetupCleanup.Foreground(windowInfos, RawForeground);

        var connected = product.ChromeCompanion.IsConnected;
        IReadOnlyList<BrowserTabInfo> rawTabs = [];
        if (connected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                rawTabs = await product.ChromeCompanion.ListTabsAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* best-effort: tolerate a slow or unresponsive companion */ }
        }
        var tabs = rawTabs.Select(t => new TabInfo(t.TabId, t.WindowId, t.Active, t.Url, t.Title,
            t.Provenance, t.SessionId, t.LastUsedSequence)).ToArray();
        var active = tabs.Where(t => t.Active).OrderByDescending(t => t.LastUsedSequence ?? long.MinValue)
            .FirstOrDefault();

        return new StateSnapshot(DateTimeOffset.UtcNow, foreground, windowInfos, connected, tabs, active,
            monitors.Length, monitors);
    }

    /// <summary>The OS foreground window read directly, for when enumeration does not list it
    /// (e.g. an untitled or tool window). Real Win32 calls.</summary>
    internal static WindowInfo? RawForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        string process;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            process = pid == 0 ? "" : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch (Exception)
        {
            process = "";
        }
        var title = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return new WindowInfo(process, title.ToString(), true, hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder text, int maxCount);
}
