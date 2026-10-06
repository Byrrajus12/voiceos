using System.Text.RegularExpressions;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Reveal is not activate. "Scroll to the customer reviews section", "take me to the pricing part", "find the FAQ section
/// on this page" ask for a part of the page to be brought into view; they never permit clicking a control that merely
/// shares the section's name (a "(4 Reviews)" link). Only activation wording permits activating a control.
/// </summary>
public static class RevealIntent
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Activation = new(@"\b(click|clicks|open|select|choose|play|activate|press|tap|hit|submit|follow)\b", Options);

    private static readonly Regex Polite = new(@"^\s*(hey\s+)?(could|can|would|will)\s+you\s+(please\s+)?|^\s*please\s+|^\s*i\s+(want|need|would\s+like)\s+(you\s+)?to\s+", Options);

    // scroll/go/jump/take me ... to <target>
    private static readonly Regex MoveTo = new(
        @"^(?:scroll|go|move|jump|skip|navigate|head|take\s+me|bring\s+me|get\s+me|send\s+me)\s+(?:(?:back\s+)?(?:down|up)\s+)?(?:to|towards|over\s+to)\s+(?:the\s+)?(?<t>.+)$", Options);
    // show/find/locate me <target> on this page | find the <target> section
    private static readonly Regex ShowOnPage = new(
        @"^(?:show|find|locate|bring\s+up)\s+(?:me\s+)?(?:the\s+)?(?<t>.+?)\s+(?:on|in)\s+(?:this|the|that|current)\s+(?:page|site|website|tab|screen)$", Options);
    private static readonly Regex FindSection = new(
        @"^(?:find|show\s+me|locate|look\s+for)\s+(?:the\s+)?(?<t>.+?\s+(?:section|part|area|portion|heading))$", Options);

    private static readonly Regex Ordinal = new(@"\b(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last|next|previous|\d+(st|nd|rd|th))\b", Options);

    private static readonly Regex TrailingPlace = new(
        @"\s+(?:on|in|of|from)\s+(?:this|the|that|current)\s+(?:page|site|website|tab|screen)$", Options);
    private static readonly Regex TrailingSection = new(@"\s+(?:section|part|area|portion|heading)$", Options);

    // A page position, not a part of the content: these are plain page scrolls, handled as such.
    private static readonly Regex PagePosition = new(
        @"^(?:the\s+)?(?:very\s+)?(?:top|bottom|end|beginning|start|middle|next\s+page|previous\s+page|page\s+\d+|\d+|it|that|there)(?:\s+of\s+(?:the\s+)?(?:page|site|website))?$", Options);

    /// <summary>The described section when the utterance asks to reveal it; null when it is anything else (including any activation wording).</summary>
    public static string? Parse(string? utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return null;
        var text = Polite.Replace(utterance.Trim().TrimEnd('.', '?', '!', ' '), "").Trim();
        if (text.Length == 0 || Activation.IsMatch(text)) return null;
        var target = (MoveTo.Match(text) is { Success: true } a ? Clean(a.Groups["t"].Value)
            : ShowOnPage.Match(text) is { Success: true } b ? Clean(b.Groups["t"].Value)
            : FindSection.Match(text) is { Success: true } c ? Clean(c.Groups["t"].Value) : null);
        if (target is null || target.Length is 0 or > 80 || PagePosition.IsMatch(target)) return null;
        if (Ordinal.IsMatch(target)) return null;
        // A destination, not a section of this page.
        if (target.Contains("://") || Regex.IsMatch(target, @"\b\w+\.(com|org|net|io|dev|app)\b", Options)) return null;
        return target;
    }

    private static string Clean(string raw)
    {
        var target = TrailingPlace.Replace(raw.Trim(), "");
        target = TrailingSection.Replace(target, "").Trim();
        return target.Trim(' ', ',', '.');
    }

    /// <summary>The wording itself permits activating a control.</summary>
    public static bool HasActivationWording(string? text) => !string.IsNullOrWhiteSpace(text) && Activation.IsMatch(text);

    /// <summary>The step only reveals: a control may not be activated to complete it.</summary>
    public static bool RevealsOnly(PlanStep step)
        => step.Reveal || step.Kind == PlanStepKind.Locate
            || step.Kind == PlanStepKind.Act && Parse(step.Description) is not null;

    /// <summary>
    /// A compiled step that only reveals a section (however the model classed it) becomes a Reveal step on that target;
    /// steps that carry activation wording or produce a value are left alone.
    /// </summary>
    public static InteractionPlan Correct(InteractionPlan plan)
    {
        var changed = false;
        var steps = plan.Steps.Select(step =>
        {
            if (step.Kind is not (PlanStepKind.Act or PlanStepKind.Locate or PlanStepKind.Open) || step.Reveal
                || step.Produces is not null || step.Query is not null) return step;
            if (Parse(step.Description) is not { } target) return step;
            changed = true;
            return step with { Kind = PlanStepKind.Locate, Target = target, Reveal = true,
                Progress = step.Progress ?? $"Scrolling to {target}" };
        }).ToArray();
        return changed ? plan with { Steps = steps } : plan;
    }
}
