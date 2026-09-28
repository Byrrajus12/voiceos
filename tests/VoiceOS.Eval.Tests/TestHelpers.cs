using VoiceOS.Core.Browser;
using VoiceOS.Eval.Live;

namespace VoiceOS.Eval.Tests;

/// <summary>Builds minimal plain-data TurnRecord/StateSnapshot fixtures for evaluator tests.
/// No product, no Windows calls — these are just DTOs.</summary>
internal static class TestHelpers
{
    public static StateSnapshot Snapshot(string? fgProcess = null, string? fgTitle = null,
        string? activeUrl = null, string? activeTitle = null)
    {
        var foreground = fgProcess is null ? null : new WindowInfo(fgProcess, fgTitle ?? "", true);
        var activeTab = activeUrl is null ? null
            : new TabInfo(1, 1, true, activeUrl, activeTitle ?? "", BrowserTabProvenance.VoiceOs, "s1", 1);
        return new StateSnapshot(DateTimeOffset.UtcNow, foreground, foreground is null ? [] : [foreground],
            activeTab is not null, activeTab is null ? [] : [activeTab], activeTab, 1);
    }

    public static TurnRecord Turn(
        OutcomeClass outcome,
        CommandRoute? route = null,
        ExecutionScopeKind? scopeKind = null,
        BrowserScopeKind? browserScopeKind = null,
        string[]? programSteps = null,
        string[]? browserOps = null,
        int tabsCreated = 0,
        int actions = 0,
        int browserDecisions = 0,
        int modelStages = 0,
        StateSnapshot? initial = null,
        StateSnapshot? final = null,
        string? failure = null,
        AttemptedDirectStep[]? attempted = null)
    {
        var routeInfo = route is null ? null : new RouteInfo(route.Value, 1, RoutingReason.None, null,
            MediaRequestKind.None, null, SemanticDestinationKind.None, null, null, TabDisposition.Unspecified,
            SurfacePreference.Unspecified, SemanticEndState.Unspecified, GoalShape.Uncertain, TaskRelation.NewTask,
            ContextDependency.Uncertain, RequestedEntityKind.Uncertain, null);
        var scopeInfo = scopeKind is null ? null : new ScopeInfo(scopeKind.Value, browserScopeKind, null, null,
            null, false, null, null, null);
        var activities = (browserOps ?? []).Select(o => new BrowserActivityInfo(o, null, null, false, null, null)).ToArray();
        var execution = new ExecutionInfo(null, null, null, null, null, programSteps ?? [], [], null, activities, attempted);
        var latency = new LatencyInfo(100, 50, [], new Dictionary<string, double>(), new Dictionary<string, int>());
        var counts = new CountInfo(actions, browserDecisions, (programSteps ?? []).Length, tabsCreated, modelStages, 0);
        var snap = Snapshot();
        return new TurnRecord(0, "test transcript", "activation-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            route?.ToString() ?? "Unrouted", outcome.ToString(), outcome, null, null, failure, routeInfo, routeInfo,
            false, scopeInfo, execution, latency, counts, initial ?? snap, final ?? snap, [], Classification.HarnessError);
    }
}
