using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Activation;

internal static class ActivityMessage
{
    public static string ForUnavailable(UnavailableReason? reason) => reason switch
    {
        UnavailableReason.IntentService => "Can't reach the command service.",
        UnavailableReason.BrowserGoalService => "Browser help is unavailable right now.",
        UnavailableReason.ChromeCompanion => "Chrome companion isn't connected.",
        _ => "The service is unavailable right now."
    };
    public static string? ForStep(VoiceStep step, IAppCatalog? catalog,
        IReadOnlyList<WindowCandidate>? windows = null) => step switch
    {
        OpenAppStep open => AppName(open.App.AppCandidateId, catalog) is { } name
            ? $"Opening {name}…" : "Opening app…",
        FocusWindowStep focus => WindowName(focus.Target, catalog, windows) is { } name
            ? $"Switching to {name}…" : "Switching window…",
        CloseWindowStep => "Closing window…",
        MinimizeWindowStep minimize => WindowName(minimize.Target, catalog, windows) is { } name
            ? $"Minimizing {name}…" : "Minimizing window…",
        MaximizeWindowStep => "Maximizing window…",
        SnapWindowStep snap => snap.Direction switch
        {
            Decision.SnapDirection.Left => "Snapping window left…",
            Decision.SnapDirection.Right => "Snapping window right…",
            _ => "Snapping window…"
        },
        MoveWindowStep => "Moving window…",
        MediaControlStep { Operation: Decision.MediaOperation.Play } => "Playing…",
        MediaControlStep { Operation: Decision.MediaOperation.Pause } => "Pausing…",
        MediaControlStep => "Controlling playback…",
        SetVolumeStep or AdjustVolumeStep => "Changing volume…",
        _ => null
    };

    private static string? AppName(string id, IAppCatalog? catalog)
    {
        var name = catalog?.FindById(id)?.DisplayName;
        return name is { Length: > 0 and <= 28 } && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.')
            ? name : null;
    }

    private static string? WindowName(VoiceTarget target, IAppCatalog? catalog,
        IReadOnlyList<WindowCandidate>? windows)
    {
        if (target is AppTarget app) return AppName(app.AppCandidateId, catalog);
        var window = target switch
        {
            CurrentWindowTarget => windows?.FirstOrDefault(static w => w.IsForeground),
            CandidateWindowTarget selected => windows?.FirstOrDefault(w => w.Id == selected.WindowCandidateId),
            _ => null
        };
        if (window?.ProcessName is not { } process) return null;
        var names = catalog?.GetAll().Where(app =>
            string.Equals(app.ProcessName, process, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return names is { Length: 1 } ? SafeLabel(names[0].DisplayName) : SafeLabel(process);
    }

    public static string ForNativeApp(string id, IAppCatalog? catalog)
        => AppName(id, catalog) is { } name ? $"Opening {name}…" : "Opening app…";

    public static string ForBrowserAction(BrowserActivity activity)
    {
        if (activity.Operation == InteractionActionKind.Scroll)
            return activity.Direction?.ToLowerInvariant() switch
            {
                "down" => "Scrolling down…",
                "up" => "Scrolling up…",
                _ => "Scrolling…"
            };
        if (activity.Operation == InteractionActionKind.GoBack) return "Going back…";
        if (activity.OpeningTab)
        {
            if (SafeLabel(activity.Query) is { } query) return $"Searching for {query}…";
            if (SafeLabel(activity.Destination) is { } destination)
                return $"Opening {destination} tab…";
            return "Opening Chrome tab…";
        }
        if (activity.Operation is InteractionActionKind.SetText or InteractionActionKind.TypeText)
            return SafeLabel(activity.Query) is { } query ? $"Searching for {query}…" : "Entering text…";
        if (activity.Operation == InteractionActionKind.Activate)
            return string.Equals(activity.TargetRole, "link", StringComparison.OrdinalIgnoreCase)
                ? SafeLabel(activity.TargetName) is { } name ? $"Opening {name}…" : "Opening result…"
                : SafeLabel(activity.Objective) is { } task
                    ? $"Working on {task}…" : "Selecting…";
        return SafeLabel(activity.Objective) is { } objective
            ? $"Working on {objective}…" : "Working in Chrome…";
    }

    private static string? SafeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 32 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' or '+' or '#')
            ? value : null;
    }

    public static string ForClarification(string? detail) => detail switch
    {
        "Chrome does not have one identifiable active tab." => "Which Chrome tab?",
        "The named tab was not uniquely identified." => "Which tab did you mean?",
        "The requested native app is unavailable or ambiguous." => "Which app did you mean?",
        "No unique installed native app matched the request." => "Which app did you mean?",
        "The execution surface is ambiguous." => "Where should I do that?",
        "The context does not safely identify a surface." => "Where should I do that?",
        "Media intent needs clarification before controlling current playback." => "What should I play?",
        _ => "Could you clarify that request?"
    };

    public static string ForFailure(string? detail) => detail switch
    {
        "Text insertion is unavailable" => "Couldn't insert that text.",
        "Managed browser interaction is unavailable" => "Chrome is unavailable.",
        "Native UI interaction is not enabled yet." => "That app action isn't available yet.",
        _ => "Couldn't complete that action."
    };
}
