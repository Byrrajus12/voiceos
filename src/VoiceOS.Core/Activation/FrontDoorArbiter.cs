using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;

namespace VoiceOS.Core.Activation;

public enum FrontDoorVerdictKind { UseRoute, RescueDirect, ContextualDirect }
public sealed record FrontDoorVerdict(FrontDoorVerdictKind Kind, IReadOnlyList<string> Reasons, VoiceProgram? Program = null);
public sealed record DirectOffer(string Summary, VoiceProgram Program);

public static class DirectMediaGuard
{
    public static bool Allows(CommandRouteDecision route, VoiceProgram? program)
        => program?.Steps.Any(s => s is MediaControlStep) != true || route.MediaRequestKind == MediaRequestKind.Transport;
}

/// <summary>Pure reconciliation of existing interpretations; never generates an action.</summary>
public static class FrontDoorArbiter
{
    public static bool IsEligible(CommandRouteDecision route, bool nativeFallback = false)
        => route.Route == CommandRoute.Clarify && route.Reason is RoutingReason.LowConfidence or RoutingReason.AmbiguousIntent
            || nativeFallback && route.Route == CommandRoute.NativeInteraction;

    public static VoiceProgram? ProgramFor(DecisionResult direct)
    {
        var compoundAttempted = direct.RawAnswers.TryGetValue("is_compound", out var compound)
            && compound.QuestionType == "noul"
            && compound.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;
        return direct.Program ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(direct.Plan));
    }

    public static FrontDoorVerdict Evaluate(CommandRouteDecision route, DecisionResult? direct,
        FrontDoorContext context, IAppCatalog? catalog, IReadOnlyList<WindowCandidate>? windows = null,
        bool nativeFallback = false, bool skipEligibility = false)
    {
        var reasons = new List<string>();
        if (!skipEligibility && !IsEligible(route, nativeFallback)) reasons.Add("route_not_eligible");
        if (direct?.ProviderFailed == true) reasons.Add("direct_unavailable");
        var program = direct is null || direct.ProviderFailed ? null : ProgramFor(direct);
        if (program is null || program.Steps.Count == 0) reasons.Add("no_program");
        if (direct is not null && program is not null)
        {
            if (program.Steps.Count == 1 && (direct.Plan.Action is VoiceAction.None or VoiceAction.Rejected || direct.Plan.RequiresClarification))
                reasons.Add("plan_not_sane");
            var policy = DirectTargetPolicy.Evaluate(route, direct.Plan, program, catalog);
            if (policy != DirectTargetVerdict.Proceed) reasons.Add("policy_" + policy);
            if (!DirectMediaGuard.Allows(route, program)) reasons.Add("media_guard");
            if (route.TabDisposition != TabDisposition.Unspecified || route.SurfacePreference == SurfacePreference.Browser
                || route.MediaRequestKind == MediaRequestKind.ContentSelection) reasons.Add("web_wording");

            bool BrowserTarget(VoiceTarget target, HashSet<string> visited) => target switch
            {
                CurrentWindowTarget => context.Foreground?.Kind == ForegroundKind.Browser,
                AppTarget app => DirectTargetPolicy.IsGenericBrowserHost(catalog?.FindById(app.AppCandidateId)),
                CandidateWindowTarget selected => DirectTargetPolicy.IsBrowserHostProcess(
                    windows?.FirstOrDefault(w => w.Id == selected.WindowCandidateId)?.ProcessName),
                StepResultTarget result when visited.Add(result.StepId) => program.Steps.FirstOrDefault(s => s.StepId == result.StepId) switch
                {
                    OpenAppStep open => DirectTargetPolicy.IsGenericBrowserHost(catalog?.FindById(open.App.AppCandidateId)),
                    { } prior when Target(prior) is { } priorTarget => BrowserTarget(priorTarget, visited),
                    _ => false
                },
                _ => false
            };
            if (program.Steps.OfType<OpenAppStep>().Any(s => DirectTargetPolicy.IsGenericBrowserHost(catalog?.FindById(s.App.AppCandidateId))))
                reasons.Add("browser_host_open");
            if (program.Steps.Any(s => Target(s) is { } target && BrowserTarget(target, [])))
                reasons.Add("browser_host_target");
        }
        if (route.RawAnswers?.TryGetValue("route", out var answer) == true && answer.HasDistribution)
        {
            var p = answer.Probabilities;
            if (Math.Max(p.GetValueOrDefault("COMPUTER_USE"), p.GetValueOrDefault("TEXT_TRANSFORM"))
                > p.GetValueOrDefault("DIRECT_CAPABILITY")) reasons.Add("route_prefers_non_direct");
        }
        return reasons.Count == 0 ? new(FrontDoorVerdictKind.RescueDirect, [], program)
            : new(FrontDoorVerdictKind.UseRoute, reasons);
    }

    private static VoiceTarget? Target(VoiceStep step) => step switch
    {
        FocusWindowStep s => s.Target, CloseWindowStep s => s.Target, MinimizeWindowStep s => s.Target,
        MaximizeWindowStep s => s.Target, SnapWindowStep s => s.Target, MoveWindowStep s => s.Target,
        _ => null
    };

    public static DirectOffer Offer(VoiceProgram program, IAppCatalog? catalog, IReadOnlyList<WindowCandidate> windows)
    {
        string Label(VoiceTarget target) => target switch
        {
            AppTarget app => catalog?.FindById(app.AppCandidateId)?.DisplayName ?? app.AppCandidateId,
            CandidateWindowTarget window => windows.FirstOrDefault(w => w.Id == window.WindowCandidateId)?.ProcessName ?? "selected window",
            CurrentWindowTarget => windows.FirstOrDefault(w => w.IsForeground)?.ProcessName ?? "foreground window",
            StepResultTarget step => "window from step " + step.StepId,
            _ => "window"
        };
        string Describe(VoiceStep step) => step switch
        {
            OpenAppStep s => "Open " + Label(s.App), FocusWindowStep s => "Focus " + Label(s.Target),
            CloseWindowStep s => "Close " + Label(s.Target), MinimizeWindowStep s => "Minimize " + Label(s.Target),
            MaximizeWindowStep s => "Maximize " + Label(s.Target), SnapWindowStep s => $"Snap {Label(s.Target)} {s.Direction}",
            MoveWindowStep s => "Move " + Label(s.Target),
            AdjustVolumeStep s => $"Turn system volume {s.Direction}", SetVolumeStep s => $"Set system volume to {s.Value}",
            MediaControlStep s => "Current media " + s.Operation,
            _ => throw new InvalidOperationException("Unknown direct step.")
        };
        return new(string.Join("; ", program.Steps.Select(Describe)), program);
    }
}
