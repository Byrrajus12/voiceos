using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Browser;

public enum BrowserTabProvenance { User, VoiceOs }

public sealed record BrowserTabInfo(int TabId, int WindowId, bool Active, string? Url,
    string? Title, BrowserTabProvenance Provenance, string? SessionId = null,
    long? LastUsedSequence = null)
{
    public Uri? Origin => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" ? new Uri(uri.GetLeftPart(UriPartial.Authority)) : null;
}

public sealed record ExecutionContextSnapshot(WindowCandidate? ForegroundWindow,
    IReadOnlyList<WindowCandidate> OpenWindows, bool BrowserConnected,
    IReadOnlyList<BrowserTabInfo> BrowserTabs, RecentTaskFrame? RecentTask = null,
    IReadOnlyList<AppCandidate>? InstalledApps = null);

public sealed record RecentTaskFrame(int TabId, string SessionId, string Url,
    string SemanticGoal, Interaction.InteractionCompletionState Completion,
    DateTimeOffset LastUsed);

public enum ExecutionScopeKind { DirectCapability, Browser, NativeInteraction, TextTransform, Clarify }
public enum BrowserScopeKind { ActiveTab, ExistingNamedTab, RecentOwnedTaskTab, NewTaskTab }

public sealed record BrowserExecutionScope(BrowserScopeKind Kind, int? TabId = null,
    string? ExpectedUrl = null, string? OwnerSessionId = null, Uri? Destination = null,
    bool ExplicitSelection = false, bool FocusOnly = false, bool RequireForegroundChrome = false,
    nint ExpectedForegroundHandle = 0, string? NamedServiceHint = null,
    SemanticEndState EndState = SemanticEndState.Unspecified,
    GoalShape GoalShape = GoalShape.Uncertain, string? ExpectedTitle = null)
{
    public bool IsSurfaceOnly => EndState == SemanticEndState.SurfaceReady
        && GoalShape == GoalShape.SurfaceOnly;
}

public sealed record NativeExecutionScope(nint WindowHandle, string ProcessName,
    string? AppCandidateId = null, SemanticEndState EndState = SemanticEndState.Unspecified);

public sealed record ExecutionScopeDecision(ExecutionScopeKind Kind,
    BrowserExecutionScope? Browser = null, string? Detail = null,
    NativeExecutionScope? Native = null);

/// <summary>Chooses a surface from typed intent and metadata. No DOM is read here.</summary>
public sealed class ScopeResolver
{
    public async ValueTask<ExecutionScopeDecision> ResolveAsync(string utterance, CommandRouteDecision route,
        ExecutionContextSnapshot context, IContextualScopeDecisionSource? contextual = null,
        CancellationToken cancellationToken = default)
    {
        if (route.Reason == RoutingReason.IncompleteIntent)
            return new(ExecutionScopeKind.Clarify, Detail: route.Detail);
        if (route.Route == CommandRoute.TextTransform)
            return new(ExecutionScopeKind.TextTransform);
        // The route describes the whole task. Context cannot turn an offered direct
        // capability into a browser task merely because its target is a browser/app.
        if (route.Route == CommandRoute.DirectCapability)
            return new(ExecutionScopeKind.DirectCapability);
        if (route.Route == CommandRoute.Clarify && route.Reason == RoutingReason.RouterFailure)
            return new(ExecutionScopeKind.Clarify, Detail: route.Detail);

        var chromeForeground = context.ForegroundWindow?.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) == true;
        var activeTabs = context.BrowserTabs.Where(t => t.Active && t.Origin is not null).Take(2).ToArray();
        BrowserTabInfo? active = activeTabs.Length == 1 ? activeTabs[0] : null;
        BrowserExecutionScope Select(BrowserTabInfo tab, BrowserScopeKind kind, bool explicitSelection = false,
            bool focusOnly = false) => new(kind, tab.TabId, tab.Url, tab.SessionId,
                ExplicitSelection: explicitSelection, FocusOnly: focusOnly,
                RequireForegroundChrome: kind == BrowserScopeKind.ActiveTab && chromeForeground,
                ExpectedForegroundHandle: kind == BrowserScopeKind.ActiveTab && chromeForeground
                    ? context.ForegroundWindow?.Hwnd ?? 0 : 0,
                NamedServiceHint: route.DestinationKind == SemanticDestinationKind.KnownService
                    ? route.DestinationName : null,
                ExpectedTitle: kind == BrowserScopeKind.ExistingNamedTab ? tab.Title : null);
        ExecutionScopeDecision Browser(BrowserExecutionScope scope)
            => new(ExecutionScopeKind.Browser, scope with { EndState = route.EndState,
                GoalShape = route.GoalShape });

        var destination = route.ExplicitUrl
            ?? ServiceResolver.Resolve(route.DestinationName)?.WebOrigin;
        if (route.DestinationKind == SemanticDestinationKind.KnownService
            && route.SurfacePreference != SurfacePreference.Browser
            && route.TabDisposition == TabDisposition.Unspecified)
        {
            var matches = (context.InstalledApps ?? []).Where(app =>
                string.Equals(app.DisplayName, route.DestinationName, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length == 0 && contextual is not null && context.InstalledApps is { Count: > 0 } candidates)
            {
                var selectedAppId = await contextual.SelectInstalledAppAsync(utterance, candidates, cancellationToken)
                    .ConfigureAwait(false);
                matches = candidates.Where(app => app.Id == selectedAppId).Take(2).ToArray();
            }
            if (matches.Length == 1)
            {
                var app = matches[0];
                var window = context.OpenWindows.FirstOrDefault(w =>
                    string.Equals(w.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
                return new(ExecutionScopeKind.NativeInteraction, Native: new(
                    window?.Hwnd ?? 0, app.ProcessName ?? "", app.Id, route.EndState));
            }
            if (route.SurfacePreference == SurfacePreference.Native)
                return new(ExecutionScopeKind.Clarify, Detail: "The requested native app is unavailable or ambiguous.");
        }
        if (route.ExplicitUrl is not null
            && route.TabDisposition is TabDisposition.CurrentTab or TabDisposition.ExistingNamedTab)
            return new(ExecutionScopeKind.Clarify,
                Detail: "An explicit URL cannot be combined safely with this tab selection yet.");
        if (route.TabDisposition == TabDisposition.NewTab)
            return Browser(new(BrowserScopeKind.NewTaskTab, Destination: destination,
                NamedServiceHint: route.DestinationKind == SemanticDestinationKind.KnownService
                    ? route.DestinationName : null));
        if (route.TabDisposition == TabDisposition.CurrentTab)
            return active is not null ? Browser(Select(active, BrowserScopeKind.ActiveTab, true))
                : new(ExecutionScopeKind.Clarify, Detail: "Chrome does not have one identifiable active tab.");
        if (route.TabDisposition == TabDisposition.ExistingNamedTab
            || route.DestinationKind == SemanticDestinationKind.NamedTab)
        {
            var selectedId = contextual is null ? null : await contextual.SelectNamedTabAsync(
                utterance, context.BrowserTabs, cancellationToken).ConfigureAwait(false);
            var selectedTab = context.BrowserTabs.SingleOrDefault(t => t.TabId == selectedId
                && t.Origin is not null);
            return selectedTab is not null
                ? Browser(Select(selectedTab, BrowserScopeKind.ExistingNamedTab, true,
                    focusOnly: route.EndState == SemanticEndState.SurfaceReady
                        && route.GoalShape == GoalShape.SurfaceOnly))
                : new(ExecutionScopeKind.Clarify, Detail: "The named tab was not uniquely identified.");
        }
        if (route.Route == CommandRoute.ComputerUse
            && route.TabDisposition == TabDisposition.Unspecified
            && route.DestinationKind == SemanticDestinationKind.None
            && route.SurfacePreference != SurfacePreference.Native
            && route.ContextDependency == ContextDependency.RequiresCurrentSurface)
            return active is not null ? Browser(Select(active, BrowserScopeKind.ActiveTab))
                : new(ExecutionScopeKind.Clarify,
                    Detail: "The required current browser surface is unavailable or ambiguous.");
        if (destination is not null)
        {
            var owned = context.BrowserTabs.Where(t => t.Provenance == BrowserTabProvenance.VoiceOs
                && t.LastUsedSequence is not null && SameDestination(t, destination, route.ExplicitUrl is not null))
                .OrderByDescending(t => t.LastUsedSequence).ToArray();
            if (owned.Length > 0 && (owned.Length == 1 || owned[0].LastUsedSequence > owned[1].LastUsedSequence))
                return Browser(Select(owned[0], BrowserScopeKind.RecentOwnedTaskTab));
            return Browser(new(BrowserScopeKind.NewTaskTab, Destination: destination,
                NamedServiceHint: route.DestinationKind == SemanticDestinationKind.KnownService
                    ? route.DestinationName : null));
        }
        if ((route.TaskRelation is TaskRelation.ContinueRecent or TaskRelation.RequiresRecent)
            && route.ContextDependency != ContextDependency.SelfContained)
            return new(ExecutionScopeKind.Clarify,
                Detail: "The request depends on prior VoiceOS work without a safe current surface or destination.");
        if (route.SurfacePreference != SurfacePreference.Browser
            && route.TabDisposition == TabDisposition.Unspecified
            && contextual is not null && context.InstalledApps is { Count: > 0 } apps)
        {
            var appId = await contextual.SelectInstalledAppAsync(utterance, apps, cancellationToken)
                .ConfigureAwait(false);
            var app = apps.SingleOrDefault(x => x.Id == appId);
            if (app is not null)
            {
                var window = context.OpenWindows.FirstOrDefault(w =>
                    string.Equals(w.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
                return new(ExecutionScopeKind.NativeInteraction, Native: new(
                    window?.Hwnd ?? 0, app.ProcessName ?? "", app.Id, route.EndState));
            }
            if (route.SurfacePreference == SurfacePreference.Native)
                return new(ExecutionScopeKind.Clarify, Detail: "No unique installed native app matched the request.");
        }
        if (route.Route == CommandRoute.ComputerUse
            && (route.ContextDependency == ContextDependency.SelfContained
                || route.TaskRelation == TaskRelation.NewTask))
            return Browser(new(BrowserScopeKind.NewTaskTab));
        // Only complete actions reach this point. Context can repair a preliminary route.
        if (contextual is null)
            return new(ExecutionScopeKind.Clarify, Detail: "The execution surface is ambiguous.");
        var offered = new List<ContextualSurface> { ContextualSurface.Clarify };
        {
            offered.Add(ContextualSurface.NewBrowserTaskTab);
            if (chromeForeground && active is not null)
                offered.Add(ContextualSurface.ActiveBrowserTab);
        }
        if (context.ForegroundWindow is { Hwnd: not 0 } foreground
            && !foreground.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
            offered.Add(ContextualSurface.ForegroundNativeWindow);
        var selected = await contextual.SelectAsync(utterance, route, context, offered, cancellationToken)
            .ConfigureAwait(false);
        if (!offered.Contains(selected)) selected = ContextualSurface.Clarify;
        return selected switch
        {
            ContextualSurface.ActiveBrowserTab when active is not null => Browser(Select(active, BrowserScopeKind.ActiveTab)),
            ContextualSurface.NewBrowserTaskTab => Browser(new(BrowserScopeKind.NewTaskTab)),
            ContextualSurface.ForegroundNativeWindow when context.ForegroundWindow is { Hwnd: not 0 } window
                => new(ExecutionScopeKind.NativeInteraction, Native: new(window.Hwnd, window.ProcessName)),
            _ => new(ExecutionScopeKind.Clarify, Detail: "The context does not safely identify a surface.")
        };
    }

    private static bool SameDestination(BrowserTabInfo tab, Uri destination, bool exact)
        => exact ? StringComparer.OrdinalIgnoreCase.Equals(tab.Url, destination.AbsoluteUri)
            : tab.Origin is { } origin && Uri.Compare(origin, destination,
                UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

}
