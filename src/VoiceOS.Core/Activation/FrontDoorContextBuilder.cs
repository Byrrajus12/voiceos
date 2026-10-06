using System.Text.RegularExpressions;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Activation;

public static class FrontDoorContextBuilder
{
    private static readonly HashSet<string> StopWords = new("the a an my this that it to on in of for and app window please".Split(' '));
    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    public static int MatchNames(string utterance, string name)
    {
        var normalized = Normalize(name);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3 && !StopWords.Contains(x)).ToArray();
        var input = Normalize(utterance);
        if (normalized.Length >= 3 && input.Contains(normalized, StringComparison.Ordinal)) return 3;
        var tokens = input.Split(' ').ToHashSet();
        if (words.Length > 0 && words.All(tokens.Contains)) return 2;
        return words.Any(x => x.Length >= 4 && tokens.Contains(x)) ? 1 : 0;
    }

    public static FrontDoorContext Build(string transcript, ExecutionContextSnapshot ctx,
        RecentTaskFrame? exposedFrame, DateTimeOffset now)
    {
        var apps = ctx.InstalledApps ?? [];
        string AppName(WindowCandidate w) => apps.FirstOrDefault(a =>
            a.AppUserModelId is not null && (a.AppUserModelId == w.AppUserModelId || a.AppUserModelId == w.ProcessAppUserModelId)
            || a.ProcessName is not null && a.ProcessName.Equals(w.ProcessName, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? w.ProcessName;
        ForegroundFact? foreground = ctx.ForegroundWindow is { } fg
            ? new(AppName(fg), fg.Title, DirectTargetPolicy.IsBrowserHostProcess(fg.ProcessName) ? ForegroundKind.Browser
                : fg.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(fg.Title)
                    ? ForegroundKind.Shell : ForegroundKind.NativeApp, fg.Hwnd) : null;
        var matches = ctx.OpenWindows.Select(w => new NamedMatch(AppName(w), NamedMatchKind.OpenWindow,
                DirectTargetPolicy.IsBrowserHostProcess(w.ProcessName), MatchNames(transcript, AppName(w))))
            .Concat(apps.Select(a => new NamedMatch(a.DisplayName, NamedMatchKind.InstalledApp,
                DirectTargetPolicy.IsBrowserHostProcess(a.ProcessName), MatchNames(transcript, a.DisplayName))))
            .Concat(ServiceResolver.DestinationChoices.Keys.Where(n => n != "None")
                .Select(n => new NamedMatch(n, NamedMatchKind.WebService, false, MatchNames(transcript, n))))
            .Where(m => m.Score >= 2).Distinct().OrderByDescending(m => m.Score).ThenBy(m => m.Kind)
            .ThenBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal).Take(5).ToArray();
        var tabs = ctx.BrowserConnected ? ctx.BrowserTabs.Where(t => t.Origin is not null).ToArray() : [];
        TabFact Fact(BrowserTabInfo t) => new(t.TabId, t.Origin!.Host, t.Title ?? "", t.Active,
            t.Provenance == BrowserTabProvenance.VoiceOs);
        var active = tabs.Where(t => t.Active).Take(2).ToArray();
        var matching = tabs.Where(t => !t.Active).Select(t => (Tab: t,
                Score: Math.Max(MatchNames(transcript, t.Origin!.Host), MatchNames(transcript, t.Title ?? ""))))
            .Where(x => x.Score >= 2).OrderByDescending(x => x.Score).ThenBy(x => x.Tab.TabId)
            .Take(3).Select(x => Fact(x.Tab)).ToArray();
        var recentTab = exposedFrame is null ? null : tabs.FirstOrDefault(t => t.TabId == exposedFrame.TabId);
        var recent = recentTab is null ? null : new RecentTaskFact(exposedFrame!.SemanticGoal, recentTab.Origin!.Host,
            exposedFrame.Completion == InteractionCompletionState.Complete,
            (int)Math.Clamp((now - exposedFrame.LastUsed).TotalSeconds, 0, int.MaxValue), recentTab.Active);
        return new(foreground, matches, new(ctx.BrowserConnected, ctx.BrowserTabs.Count,
            active.Length == 1 ? Fact(active[0]) : null, matching), recent);
    }
}
