using VoiceOS.Core.Apps;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Activation;

internal static class ActivityMessage
{
    public static string? ForStep(VoiceStep step, IAppCatalog? catalog) => step switch
    {
        OpenAppStep open => AppName(open.App.AppCandidateId, catalog) is { } name
            ? $"Opening {name}…" : "Opening app…",
        FocusWindowStep => "Switching window…",
        CloseWindowStep => "Closing window…",
        MinimizeWindowStep => "Minimizing window…",
        MaximizeWindowStep => "Maximizing window…",
        SnapWindowStep => "Snapping window…",
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

    public static string ForBrowserAction(InteractionActionKind? action) => action switch
    {
        InteractionActionKind.Scroll => "Scrolling…",
        InteractionActionKind.GoBack => "Going back…",
        InteractionActionKind.Activate => "Selecting in Chrome…",
        InteractionActionKind.SetText or InteractionActionKind.TypeText => "Entering text…",
        _ => "Working in Chrome…"
    };

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
        "Empty transcript" => "I didn't catch that.",
        "Text insertion is unavailable" => "Couldn't insert that text.",
        "Managed browser interaction is unavailable" => "Chrome is unavailable.",
        "Native UI interaction is not enabled yet." => "That app action isn't available yet.",
        _ => "Couldn't complete that action."
    };
}
