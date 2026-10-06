using System.Text.RegularExpressions;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Deterministic, site-agnostic reading of playback from the page's own controls: a media control that offers to pause (and none
/// that offers to play) means the media is playing. Used to reconcile a "play it" step with what the preceding step already did.
/// </summary>
internal static partial class BrowserMediaState
{
    private static readonly HashSet<string> NotThisResource = new(StringComparer.OrdinalIgnoreCase)
        { "next", "previous", "prev", "another", "different", "again", "pause", "stop", "all", "playlist", "queue" };

    /// <summary>A bare "play it": the step's own words name the operation only and do not point at a different item.</summary>
    public static bool IsPlayRequest(PlanStep step)
    {
        if (step.Kind != PlanStepKind.Act) return false;
        var words = BrowserCompletionEvidence.Tokens(step.Description).ToArray();
        return words.Length > 0 && words[0] is "play" or "resume" && !words.Any(NotThisResource.Contains);
    }

    /// <summary>A control offering to pause is present and no control offers to play: playback is positively observed.</summary>
    public static bool IsPlaying(string? evidence)
    {
        var names = BrowserEvidence.Elements(evidence).Where(static e => e.Enabled && e.Role is "button" or "link" or null)
            .Select(static e => (e.Name ?? "").Trim()).ToArray();
        return names.Any(static n => PauseControl().IsMatch(n)) && !names.Any(static n => PlayControl().IsMatch(n));
    }

    /// <summary>
    /// Playback is established for the media the previous step just opened: a control offering to pause, or (when no control offers to
    /// play) a "now playing" state control while the page is the surface that activation led to. "Now playing" text alone, on a page the
    /// activation did not lead to, proves nothing.
    /// </summary>
    public static bool EstablishedPlaying(string? evidence, IReadOnlyList<Effect> priorEffects)
    {
        if (IsPlaying(evidence)) return true;
        var names = BrowserEvidence.Elements(evidence).Where(static e => e.Enabled).Select(static e => (e.Name ?? "").Trim()).ToArray();
        if (!names.Any(static n => NowPlayingState().IsMatch(n)) || names.Any(static n => PlayControl().IsMatch(n))) return false;
        var activated = priorEffects.Select((e, i) => (e, i)).LastOrDefault(static x => x.e.Kind == EffectKind.Activated);
        if (activated.e is null) return false;
        var led = priorEffects.Skip(activated.i + 1).Any(static e => e.Kind == EffectKind.Navigated)
            || TargetAmbiguity.CanonicalHref(activated.e.Subject?.Href) is { } opened && opened == TargetAmbiguity.CanonicalHref(BrowserEvidence.Url(evidence));
        return led;
    }

    [GeneratedRegex(@"^(\d{1,2}(:\d{2}){1,2}\s+)?now playing$", RegexOptions.IgnoreCase)]
    private static partial Regex NowPlayingState();

    [GeneratedRegex(@"^pause(\s*\(.*\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PauseControl();

    [GeneratedRegex(@"^play(\s*\(.*\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayControl();
}
