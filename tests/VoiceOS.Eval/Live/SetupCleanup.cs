namespace VoiceOS.Eval.Live;

/// <summary>A tab the setup opened: closable only while its window keeps another tab, so cleanup
/// never closes a whole browser window it cannot identify as setup-created.</summary>
public sealed record SetupTabToClose(int TabId, int WindowId, string Url);

public sealed record SetupCleanupPlan(IReadOnlyList<SetupTabToClose> Tabs, IReadOnlyList<long> Windows,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Pure plan for undoing what a scenario's setup created, so later scenarios do not inherit
/// duplicate pages or app windows. Only state that is identifiable as setup-created is touched:
/// web tabs that appeared between the pre-setup and post-setup snapshots, and top-level windows of
/// a setup-started (non-browser) process that did not exist before setup.
/// </summary>
public static class SetupCleanup
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge" };

    public static IReadOnlyList<string> StartedProcessNames(IEnumerable<SetupStep> setup)
        => setup.Where(s => s.Kind == SetupStepKind.StartProcess && !string.IsNullOrWhiteSpace(s.File))
            .Select(s => Path.GetFileNameWithoutExtension(s.File!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static SetupCleanupPlan Plan(IEnumerable<SetupStep> setup, StateSnapshot beforeSetup,
        StateSnapshot afterSetup, StateSnapshot current)
    {
        var skipped = new List<string>();
        var started = StartedProcessNames(setup);

        var tabs = new List<SetupTabToClose>();
        if (started.Any(BrowserProcesses.Contains)
            && beforeSetup.BrowserConnected && afterSetup.BrowserConnected && current.BrowserConnected)
        {
            var before = beforeSetup.Tabs.Select(t => t.TabId).ToHashSet();
            var created = afterSetup.Tabs.Where(t => !before.Contains(t.TabId) && t.Origin is not null)
                .Select(t => t.TabId).ToHashSet();
            foreach (var tab in current.Tabs.Where(t => created.Contains(t.TabId)))
            {
                if (tab.Url is null) continue;
                if (current.Tabs.Count(t => t.WindowId == tab.WindowId) < 2)
                    skipped.Add($"tab {tab.TabId} is the only tab in its window");
                else
                    tabs.Add(new(tab.TabId, tab.WindowId, tab.Url));
            }
        }

        var beforeWindows = beforeSetup.Windows.Select(w => w.Hwnd).ToHashSet();
        var appProcesses = started.Where(p => !BrowserProcesses.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows = current.Windows.Where(w => w.Hwnd != 0 && !beforeWindows.Contains(w.Hwnd)
                && appProcesses.Contains(w.ProcessName))
            .Select(w => w.Hwnd).Distinct().ToArray();
        return new(tabs, windows, skipped);
    }

    /// <summary>The enumerated foreground window, else the raw foreground window (marked as not
    /// enumerated) so an unlisted foreground is recorded instead of reading as none.</summary>
    public static WindowInfo? Foreground(IReadOnlyList<WindowInfo> enumerated, Func<WindowInfo?> rawForeground)
    {
        var listed = enumerated.FirstOrDefault(w => w.IsForeground);
        if (listed is not null) return listed;
        return rawForeground() is { } raw ? raw with { IsForeground = true, Enumerated = false } : null;
    }
}
