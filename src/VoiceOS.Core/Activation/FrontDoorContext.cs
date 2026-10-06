using System.Text.Json;

namespace VoiceOS.Core.Activation;

public enum ForegroundKind { NativeApp, Browser, Shell, None }
public enum NamedMatchKind { OpenWindow, InstalledApp, WebService }
public sealed record ForegroundFact(string AppName, string Title, ForegroundKind Kind, nint Hwnd);
public sealed record NamedMatch(string Name, NamedMatchKind Kind, bool IsBrowserHost, int Score);
public sealed record TabFact(int TabId, string Site, string Title, bool Active, bool OpenedByVoiceOs);
public sealed record BrowserFacts(bool CompanionConnected, int OpenTabCount, TabFact? ActiveTab,
    IReadOnlyList<TabFact> MatchingTabs);
public sealed record RecentTaskFact(string Goal, string Site, bool Completed, int SecondsAgo, bool StillActiveTab);

/// <summary>Bounded interpretation facts. Handles, inventories and page content never cross this boundary.</summary>
public sealed record FrontDoorContext(ForegroundFact? Foreground, IReadOnlyList<NamedMatch> NamedMatches,
    BrowserFacts Browser, RecentTaskFact? RecentTask)
{
    public const int MaxSerializedChars = 1500;
    internal static string Clean(string? value, int limit)
        => new string((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray());

    public object ToJevState()
    {
        var tabs = Browser.CompanionConnected ? Browser.MatchingTabs.Take(3).ToArray() : [];
        var matches = NamedMatches.Take(5).ToArray();
        var titleLimit = 80;
        var textLimit = 120;
        string Text(string? value, int limit) => Clean(value, Math.Min(limit, textLimit));
        object Tab(TabFact tab) => new { site = Text(tab.Site, 60), title = Text(tab.Title, titleLimit),
            opened_by = tab.OpenedByVoiceOs ? "voiceos" : "user" };
        object State() => new {
            foreground = Foreground is null ? null : new Dictionary<string, object> {
                ["app"] = Text(Foreground.AppName, 40),
                ["kind"] = Foreground.Kind switch { ForegroundKind.NativeApp => "native_app",
                    ForegroundKind.Browser => "browser", ForegroundKind.Shell => "shell", _ => "none" }
            }.Concat(Foreground.Kind == ForegroundKind.Browser ? [] :
                new[] { new KeyValuePair<string, object>("title", Text(Foreground.Title, titleLimit)) })
                .ToDictionary(x => x.Key, x => x.Value),
            named_matches = matches.Select(m => new { name = Text(m.Name, 40),
                kind = m.Kind switch { NamedMatchKind.OpenWindow => "open_window",
                    NamedMatchKind.InstalledApp => "installed_app", _ => "web_service" }, browser = m.IsBrowserHost }).ToArray(),
            browser = new { companion = Browser.CompanionConnected ? "connected" : "disconnected",
                open_tabs = Browser.CompanionConnected ? Browser.OpenTabCount : 0,
                active_tab = Browser.CompanionConnected && Browser.ActiveTab is { } active ? Tab(active) : null,
                matching_tabs = tabs.Select(Tab).ToArray() },
            recent_task = RecentTask is null ? null : new { goal = Text(RecentTask.Goal, 120),
                site = Text(RecentTask.Site, 60), completed = RecentTask.Completed,
                seconds_ago = RecentTask.SecondsAgo, still_active_tab = RecentTask.StillActiveTab }
        };
        object state = State();
        // Reduce in the prescribed order, accounting for JSON escaping as well as field lengths.
        while (JsonSerializer.Serialize(state).Length > MaxSerializedChars)
        {
            if (tabs.Length > 0) tabs = tabs[..^1];
            else if (matches.Length > 3) matches = matches[..^1];
            else if (titleLimit > 40) titleLimit = 40;
            else if (textLimit > 20) textLimit /= 2;
            else throw new InvalidOperationException("Compact context cannot fit its serialization bound.");
            state = State();
        }
        return state;
    }
}
