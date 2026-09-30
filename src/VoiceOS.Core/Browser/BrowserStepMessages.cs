using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// User-facing wording for what a browser step is doing and for why it stopped. Failures name the actual blocker,
/// never a model confidence, and never ask the user to "clarify" something only the page prevented.
/// </summary>
public static class BrowserStepMessages
{
    public const string NoSearchControl = "I couldn't find a usable search control on this page.";
    public const string NoChange = "That action didn't change the page.";
    public const string NoMatch = "I couldn't find a matching result.";
    public const string Stalled = "The page stopped responding.";
    public const string TooLong = "I couldn't finish that in time.";
    public const string NotUnderstood = "I couldn't work out the steps for that request.";

    private static readonly string[] Known =
        [NoSearchControl, NoChange, NoMatch, Stalled, TooLong, NotUnderstood,
            "There is no earlier page in this tab.", "That tab has no previous page.", "That tab has no next page.", "Nothing was done on the current page yet."];

    /// <summary>True for a message this class already worded for the user (shown as is).</summary>
    public static bool IsUserFacing(string? detail)
        => detail is not null && (Known.Contains(detail)
            || detail.StartsWith("Couldn't find ", StringComparison.Ordinal) || detail.StartsWith("I couldn't ", StringComparison.Ordinal)
            || detail.StartsWith("That ", StringComparison.Ordinal) && detail.EndsWith('.'));

    public static string Failure(InteractionPlan plan, string? code, string? detail, InteractionObservation? observation,
        string? repairReason = null)
    {
        if (code == "no_history") return "There is no earlier page in this tab.";
        if (code == "impossible" && SafeReason(repairReason) is { } reason) return reason;
        var step = plan.Current;
        var afterSearch = plan.Completed.Any(static s => s.Kind == PlanStepKind.Search);
        var target = Short(step.Target);
        switch (step.Kind)
        {
            case PlanStepKind.History:
                return "The browser action didn't change the page.";
            case PlanStepKind.Reach:
                return target is null ? "I couldn't reach that site." : $"I couldn't reach {target}.";
            case PlanStepKind.Search:
                var hasSearch = observation is not null
                    && BrowserEvidence.Elements(observation.Evidence).Any(static e => e.Enabled && e.Kind == BrowserDomFacts.SearchField);
                return hasSearch ? "That search didn't go through." : NoSearchControl;
            case PlanStepKind.Locate:
                return target is null ? NoMatch
                    : $"Couldn't find {target} {(afterSearch ? "in these results" : "on this page")}.";
            case PlanStepKind.Open:
                return code is "no_progress" or "repeated_failure" or "proof_refuted" ? NoChange
                    : target is null ? NoMatch : $"I couldn't open {target}.";
        }
        return code switch
        {
            "no_progress" or "repeated_failure" or "completion_unconfirmed" or "no_action" => NoChange,
            "budget_exhausted" => TooLong,
            _ => "I couldn't do that on this page."
        };
    }

    /// <summary>A short label safe to show: bounded, printable, no control characters.</summary>
    private static string? Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= 40 && text.All(static c => !char.IsControl(c)) ? text : null;
    }

    private static string? SafeReason(string? reason)
        => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 120 && reason.All(static c => !char.IsControl(c))
            ? (reason.EndsWith('.') ? reason : reason + ".") : null;
}
