namespace VoiceOS.Eval.Live;

/// <summary>
/// Pure verification of a <see cref="WindowExpectation"/> against before/after window probes.
/// Geometry is judged with a tolerance; the foreground window is irrelevant (Snap Assist or the
/// shell may take focus after a successful snap).
/// </summary>
public static class WindowExpectationEvaluator
{
    /// <summary>Returns the check plus whether the selected windows were identical before and after
    /// (used to tell FalseSuccess from WrongTarget when the check fails).</summary>
    public static (CheckResult Check, bool Unchanged) Evaluate(WindowExpectation expect, int index,
        StateSnapshot initial, StateSnapshot final)
    {
        var name = $"final.windows[{index}]";
        var before = Select(expect, initial, initial).ToArray();
        var after = Select(expect, initial, final).ToArray();
        var initialByHwnd = initial.Windows.Where(static w => w.Hwnd != 0)
            .GroupBy(static w => w.Hwnd).ToDictionary(static g => g.Key, static g => g.First());
        var unchanged = before.Length == after.Length
            && after.All(w => initialByHwnd.TryGetValue(w.Hwnd, out var prior) && Same(prior, w));

        if (!expect.Exists)
            return (Check(name, expect, after.Length == 0, after.Length == 0 ? "absent" : Describe(after)), unchanged);
        if (after.Length == 0)
            return (Check(name, expect, false, "no matching window"), unchanged);

        // Judge the windows the turn affected when there are any; otherwise whatever matches.
        var changed = after.Where(w => !initialByHwnd.TryGetValue(w.Hwnd, out var prior) || !Same(prior, w)).ToArray();
        var pool = expect.IsNew ? after.Where(w => !initialByHwnd.ContainsKey(w.Hwnd)).ToArray()
            : changed.Length > 0 ? changed : after;
        var satisfied = pool.FirstOrDefault(w => Satisfies(expect, w, initialByHwnd, final));
        return (Check(name, expect, satisfied is not null,
            pool.Length == 0 ? "no new matching window" : Describe(satisfied is null ? pool : [satisfied])), unchanged);
    }

    /// <summary>Approximately the left/right half of the work area of the window's own monitor.</summary>
    public static bool IsSnapped(WindowInfo window, SnapSide side, StateSnapshot snapshot)
    {
        if (window.Rect is not { } r || window.IsMinimized || window.IsMaximized) return false;
        var area = snapshot.Monitors?.FirstOrDefault(m => m.Index == window.MonitorIndex)?.WorkArea;
        if (area is null) return false;
        var tolX = Math.Max(16, area.Width * 0.04);
        var tolY = Math.Max(16, area.Height * 0.04);
        var halfWidth = Math.Abs(r.Width - area.Width / 2.0) <= tolX;
        var fullHeight = Math.Abs(r.Top - area.Top) <= tolY && Math.Abs(r.Height - area.Height) <= tolY;
        var edge = side == SnapSide.Left
            ? Math.Abs(r.Left - area.Left) <= tolX
            : Math.Abs(r.Right - area.Right) <= tolX;
        return halfWidth && fullHeight && edge;
    }

    private static IEnumerable<WindowInfo> Select(WindowExpectation e, StateSnapshot initial, StateSnapshot snapshot)
    {
        var windows = snapshot.Windows.AsEnumerable();
        if (e.InitialForeground)
        {
            var hwnd = initial.Foreground?.Hwnd ?? 0;
            windows = windows.Where(w => hwnd != 0 && w.Hwnd == hwnd);
        }
        if (e.Process is { } process)
            windows = windows.Where(w => string.Equals(w.ProcessName, process, StringComparison.OrdinalIgnoreCase));
        if (e.TitleContains is { } title)
            windows = windows.Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        return windows;
    }

    private static bool Satisfies(WindowExpectation e, WindowInfo w, IReadOnlyDictionary<long, WindowInfo> initialByHwnd,
        StateSnapshot final)
        => (e.Minimized is not { } min || w.IsMinimized == min)
            && (e.Maximized is not { } max || w.IsMaximized == max)
            && (e.Snapped is not { } side || IsSnapped(w, side, final))
            && (!e.MonitorChanged || initialByHwnd.TryGetValue(w.Hwnd, out var prior)
                && prior.MonitorIndex is not null && w.MonitorIndex is not null && prior.MonitorIndex != w.MonitorIndex);

    private static bool Same(WindowInfo a, WindowInfo b)
        => a.Rect == b.Rect && a.IsMinimized == b.IsMinimized && a.IsMaximized == b.IsMaximized
            && a.MonitorIndex == b.MonitorIndex;

    private static CheckResult Check(string name, WindowExpectation expect, bool passed, string actual)
        => new(name, CheckCategory.EndState, passed, expect.Describe(), actual);

    private static string Describe(IEnumerable<WindowInfo> windows) => string.Join("; ", windows.Take(3).Select(static w =>
        $"{w.ProcessName}#{w.Hwnd} rect={(w.Rect is { } r ? $"{r.Left},{r.Top},{r.Width}x{r.Height}" : "?")} " +
        $"min={w.IsMinimized} max={w.IsMaximized} monitor={w.MonitorIndex?.ToString() ?? "?"}"));
}
