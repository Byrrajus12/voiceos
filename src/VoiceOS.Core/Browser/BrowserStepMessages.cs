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
    public const string Captcha = "This page requires a CAPTCHA before I can continue.";
    public const string SignIn = "You need to sign in before I can continue.";
    public const string Verification = "This page needs a verification code before I can continue.";
    public const string Payment = "This page needs a payment confirmation from you before I can continue.";
    public const string HumanOther = "This page needs something only you can do before I can continue.";
    public const string ChoiceExpired = "That choice has expired.";
    public const string ChoiceGone = "I couldn't find that option on the page any more.";
    public const string ChoiceAmbiguous = "That option now matches more than one thing on the page.";
    public const string ChoiceUnknown = "That isn't one of the choices.";

    private static readonly string[] Known =
        [NoSearchControl, NoChange, NoMatch, Stalled, TooLong, NotUnderstood,
            "There is no earlier page in this tab.", "That tab has no previous page.", "That tab has no next page.", "Nothing was done on the current page yet.",
            Captcha, SignIn, Verification, Payment, HumanOther, ChoiceExpired, ChoiceGone, ChoiceAmbiguous, ChoiceUnknown];

    /// <summary>True for a message this class already worded for the user (shown as is).</summary>
    public static bool IsUserFacing(string? detail)
        => detail is not null && (Known.Contains(detail)
            || detail.StartsWith("Couldn't find ", StringComparison.Ordinal) || detail.StartsWith("I couldn't ", StringComparison.Ordinal)
            || detail.StartsWith("I reached the end of the page", StringComparison.Ordinal)
            || detail.StartsWith("That ", StringComparison.Ordinal) && detail.EndsWith('.'));

    public static string Failure(InteractionPlan plan, string? code, string? detail, InteractionObservation? observation,
        string? repairReason = null)
    {
        if (code == "no_history") return "There is no earlier page in this tab.";
        if (code == "human_required" && IsUserFacing(detail)) return detail!;
        if (code == "no_value")
            return Short(plan.Current.Target ?? plan.Current.Description) is { } wanted
                ? $"I looked, but couldn't determine {wanted}." : "I couldn't find the information that step needed.";
        if (code == "impossible" && SafeReason(repairReason) is { } reason) return reason;
        var step = plan.Current;
        if (code == "weak_target" && step.Kind == PlanStepKind.Act) return "I couldn't find a clear match for that on this page.";
        if (code == "ambiguous_target") return "I couldn't tell which one you meant on this page.";
        var afterSearch = plan.Completed.Any(static s => s.Kind == PlanStepKind.Search);
        var target = Short(step.Target);
        // Only a document bottom that was actually observed is "the end of the page"; running out of recovery is not.
        if (step.Kind is PlanStepKind.Open or PlanStepKind.Locate && target is not null && observation is not null
            && code is null or "budget_exhausted" or "blocked" or "no_action" or "weak_target" or "low_operation_confidence"
                or "correction_exhausted" or "unoffered_operation" or "invalid_target" or "ambiguous_target"
            && BrowserEvidence.AtDocumentEnd(observation.Evidence))
            return $"I reached the end of the page but couldn't find {target}.";
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
