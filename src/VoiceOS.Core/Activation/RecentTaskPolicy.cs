using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Activation;

/// <summary>Only deterministic invalidity deletes memory. Model judgments affect this turn's exposure.</summary>
public static class RecentTaskPolicy
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public static (RecentTaskFrame? Stored, RecentTaskFrame? Exposed, string Reason) Validate(
        RecentTaskFrame? stored, bool inventoryKnown, IReadOnlyList<BrowserTabInfo> tabs, DateTimeOffset now)
    {
        if (stored is null) return (null, null, "absent");
        if (now - stored.LastUsed > Ttl) return (null, null, "expired");
        if (!inventoryKnown) return (stored, null, "inventory_unavailable");
        if (!tabs.Any(tab => tab.TabId == stored.TabId && tab.SessionId == stored.SessionId
            && StringComparer.Ordinal.Equals(tab.Url, stored.Url)))
            return (null, null, "tab_invalid");
        return (stored, stored, "validated");
    }

    public static RecentTaskFrame? ExposedForScope(RecentTaskFrame? exposed, CommandRouteDecision route)
        => route.TaskRelationEstablished && route.TaskRelation == TaskRelation.NewTask ? null : exposed;

    public static RecentTaskFrame? AfterBrowserRun(RecentTaskFrame? stored, BrowserInteractionOutcome outcome,
        string transcript, DateTimeOffset now)
        => outcome.Unavailable is null
            && outcome.Completion is InteractionCompletionState.Complete or InteractionCompletionState.Uncertain
            && outcome is { TabId: int tabId, SessionId: { } sessionId, Url: { } url }
            ? new(tabId, sessionId, url, outcome.SemanticGoal ?? transcript, outcome.Completion, now) : stored;
}
