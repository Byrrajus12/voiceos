using VoiceOS.Core.Candidates;
using VoiceOS.Core.Activation;
using System.Diagnostics;

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
    IReadOnlyList<AppCandidate>? InstalledApps = null,
    Activation.FrontDoorContext? FrontDoor = null)
{
    public DirectOffer? SafeDirectOffer { get; init; }
}

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
    GoalShape GoalShape = GoalShape.Uncertain, string? ExpectedTitle = null,
    bool DestinationPending = false, bool TabClaimRefuted = false)
{
    public bool BlankTabRequested { get; init; }
    public bool AcquiresSurface => Kind switch
    {
        BrowserScopeKind.NewTaskTab => Destination is not null
            || ServiceResolver.Resolve(NamedServiceHint) is not null || BlankTabRequested,
        BrowserScopeKind.ExistingNamedTab or BrowserScopeKind.RecentOwnedTaskTab => FocusOnly,
        BrowserScopeKind.ActiveTab => ExplicitSelection && FocusOnly,
        _ => false
    };
    /// <summary>Acquiring the surface is the whole goal. A requested entity whose destination is
    /// not yet known (DestinationPending) still needs navigation, so the surface alone never
    /// completes it. A current tab reached because the inventory refuted a named-tab claim
    /// (TabClaimRefuted) was not requested as a surface, so arriving there completes nothing:
    /// the requested content action still has to run.</summary>
    public bool IsSurfaceOnly => EndState == SemanticEndState.SurfaceReady
        && GoalShape == GoalShape.SurfaceOnly && !DestinationPending && !TabClaimRefuted && AcquiresSurface;
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
        CancellationToken cancellationToken = default,
        Func<CancellationToken, ValueTask<DirectOffer?>>? directOffer = null, bool grounded = true)
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

        var chromeForeground = context.BrowserConnected
            && context.ForegroundWindow?.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) == true;
        var activeTabs = context.BrowserConnected
            ? context.BrowserTabs.Where(t => t.Active && t.Origin is not null).Take(2).ToArray() : [];
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
                GoalShape = route.GoalShape,
                BlankTabRequested = scope.Kind == BrowserScopeKind.NewTaskTab
                    && (route.TabDisposition == TabDisposition.NewTab || route.RequestedEntity == RequestedEntityKind.BrowserItself),
                // A generic tab does not satisfy a named entity: without a tab, destination, or
                // registered service, the browser must still discover and reach it.
                DestinationPending = route.RequestsNamedEntity && scope.TabId is null
                    && scope.Destination is null && ServiceResolver.Resolve(scope.NamedServiceHint) is null });

        // Going back without media or navigation evidence: the router leaves it unresolved and
        // only a browser surface the user is looking at resolves it (as navigation). Ambient
        // playback is never evidence for media Previous; anything else clarifies.
        if (route.Reason == RoutingReason.UnresolvedReturn)
            return chromeForeground && active is not null && !route.RequestsNamedEntity
                ? Browser(Select(active, BrowserScopeKind.ActiveTab))
                : new(ExecutionScopeKind.Clarify, Detail: route.Detail);

        var destination = route.ExplicitUrl
            ?? ServiceResolver.Resolve(route.DestinationName)?.WebOrigin;
        // A web-content task becomes native only through an explicit native preference or a
        // goal that is solely acquiring the app surface; context and app inventory cannot infer it.
        var nativeInferable = route.Route != CommandRoute.ComputerUse
            || route.SurfacePreference == SurfacePreference.Native
            || route.GoalShape == GoalShape.SurfaceOnly;
        if (route.DestinationKind == SemanticDestinationKind.KnownService
            && route.SurfacePreference != SurfacePreference.Browser
            && route.TabDisposition == TabDisposition.Unspecified
            && nativeInferable)
        {
            var service = ServiceResolver.Resolve(route.DestinationName);
            var matches = (context.InstalledApps ?? []).Where(app =>
                service is not null && ServiceResolver.IsRepresentedBy(service, app.DisplayName))
                .Take(2).ToArray();
            // Explicit Native requires an installed representation of the entity itself; a
            // contextual pick of another app can never satisfy it.
            if (matches.Length == 0 && route.SurfacePreference != SurfacePreference.Native
                && contextual is not null && context.InstalledApps is { Count: > 0 } candidates)
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
        var weakNamedTab = false;
        if (route.TabDisposition == TabDisposition.ExistingNamedTab
            || route.DestinationKind == SemanticDestinationKind.NamedTab)
        {
            // ExistingNamedTab is a semantic claim; the real tab inventory settles it.
            var selection = contextual is null ? NamedTabSelection.Unavailable("no_named_tab_picker")
                : await contextual.SelectNamedTabAsync(utterance, context.BrowserTabs, cancellationToken)
                    .ConfigureAwait(false);
            var focusOnly = route.EndState == SemanticEndState.SurfaceReady && route.GoalShape == GoalShape.SurfaceOnly;
            switch (selection.Kind)
            {
                case NamedTabSelectionKind.Selected:
                    var selectedTab = context.BrowserTabs.SingleOrDefault(t => t.TabId == selection.TabId
                        && t.Origin is not null);
                    if (selectedTab is null) break;
                    // The named tab is already the one in view: it is the current surface.
                    return chromeForeground && active?.TabId == selectedTab.TabId
                        ? Browser(Select(selectedTab, BrowserScopeKind.ActiveTab, true, focusOnly))
                        : Browser(Select(selectedTab, BrowserScopeKind.ExistingNamedTab, true, focusOnly));
                case NamedTabSelectionKind.Ambiguous:
                    return new(ExecutionScopeKind.Clarify, Detail: "More than one open tab matches that name.");
                case NamedTabSelectionKind.NoMatch:
                    // The claim is refuted. Only a request that itself requires the visible surface,
                    // with Chrome visibly in front and one identifiable active tab, runs there; it
                    // never completes merely by being on that page.
                    if (route.Route == CommandRoute.ComputerUse
                        && route.ContextDependency == ContextDependency.RequiresCurrentSurface
                        && route.SurfacePreference != SurfacePreference.Native
                        && chromeForeground && active is not null)
                        return Browser(Select(active, BrowserScopeKind.ActiveTab) with { TabClaimRefuted = true });
                    return new(ExecutionScopeKind.Clarify, Detail: "No open tab matches that name.");
            }
            weakNamedTab = grounded && selection.Kind == NamedTabSelectionKind.Unavailable
                && selection.Reason == "choice_confidence_below_threshold";
            if (!weakNamedTab)
                return new(ExecutionScopeKind.Clarify, Detail: "The named tab was not uniquely identified.");
        }
        // Current-surface dependence is its own dimension, independent of task relation: a new
        // task can still target what is visible now. A service destination alone does not
        // relocate such a task: it is the visible surface's own service or an entity on it, and
        // it cannot send the task to a new or recent tab while a current browser surface is in
        // view. The destination keeps precedence only when it is corroborated as the task's
        // surface (a literal URL, an explicit web-surface preference, or a goal that is solely
        // acquiring that surface), or when no current browser surface is in view.
        if (route.Route == CommandRoute.ComputerUse
            && (route.TabDisposition == TabDisposition.Unspecified || weakNamedTab)
            && route.SurfacePreference != SurfacePreference.Native
            && route.ContextDependency == ContextDependency.RequiresCurrentSurface)
        {
            if (route.DestinationKind == SemanticDestinationKind.None)
                return active is not null ? Browser(Select(active, BrowserScopeKind.ActiveTab))
                    : new(ExecutionScopeKind.Clarify,
                        Detail: "The required current browser surface is unavailable or ambiguous.");
            if (route.DestinationKind == SemanticDestinationKind.KnownService && active is not null
                && destination is not null
                && route.SurfacePreference != SurfacePreference.Browser
                && route.GoalShape != GoalShape.SurfaceOnly)
            {
                var activeIsDestination = SameDestination(active, destination, false);
                if (activeIsDestination || chromeForeground)
                    return Browser(Select(active, BrowserScopeKind.ActiveTab) with
                    {
                        NamedServiceHint = activeIsDestination ? route.DestinationName : null
                    });
            }
        }
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
            && route.ContextDependency != ContextDependency.SelfContained
            && !(grounded && chromeForeground && active is not null))
            return new(ExecutionScopeKind.Clarify,
                Detail: "The request depends on prior VoiceOS work without a safe current surface or destination.");
        if (route.SurfacePreference != SurfacePreference.Browser
            && route.TabDisposition == TabDisposition.Unspecified && nativeInferable
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
        // Content selection without a destination leaves its provider unnamed. When a usable
        // current browser surface exists, context judges whether it is that provider.
        var unnamedContentProvider = route.MediaRequestKind == MediaRequestKind.ContentSelection
            && contextual is not null && chromeForeground && active is not null;
        if (route.Route == CommandRoute.ComputerUse
            && (route.ContextDependency == ContextDependency.SelfContained
                || route.TaskRelation == TaskRelation.NewTask)
            && !unnamedContentProvider)
            return Browser(new(BrowserScopeKind.NewTaskTab));
        // Only complete actions reach this point. Context can repair a preliminary route.
        if (contextual is null)
            return new(ExecutionScopeKind.Clarify, Detail: "The execution surface is ambiguous.");
        var offered = new List<ContextualSurface> { ContextualSurface.Clarify };
        var safeDirect = directOffer is null ? null : await directOffer(cancellationToken).ConfigureAwait(false);
        if (safeDirect is not null)
        {
            offered.Add(ContextualSurface.DirectCapability);
            context = context with { SafeDirectOffer = safeDirect };
        }
        var recentTab = context.RecentTask is { } frame && context.BrowserConnected
            ? context.BrowserTabs.FirstOrDefault(t => t.TabId == frame.TabId && t.SessionId == frame.SessionId
                && t.Url == frame.Url && t.Active && t.Provenance == BrowserTabProvenance.VoiceOs && t.Origin is not null)
            : null;
        if (grounded && recentTab is not null && route.TaskRelationEstablished
            && route.TaskRelation is TaskRelation.ContinueRecent or TaskRelation.RequiresRecent
            && !(chromeForeground && context.BrowserTabs.Any(t => t.Active && t.Origin is not null && t.TabId != recentTab.TabId)))
            offered.Add(ContextualSurface.RecentOwnedBrowserTab);
        {
            offered.Add(ContextualSurface.NewBrowserTaskTab);
            if (chromeForeground && active is not null)
                offered.Add(ContextualSurface.ActiveBrowserTab);
        }
        if (nativeInferable && context.ForegroundWindow is { Hwnd: not 0 } foreground
            && !foreground.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
            offered.Add(ContextualSurface.ForegroundNativeWindow);
        var selected = await contextual.SelectAsync(utterance, route, context, offered, cancellationToken)
            .ConfigureAwait(false);
        if (!offered.Contains(selected)) selected = ContextualSurface.Clarify;
        return selected switch
        {
            ContextualSurface.ActiveBrowserTab when active is not null => Browser(Select(active, BrowserScopeKind.ActiveTab)),
            ContextualSurface.RecentOwnedBrowserTab when recentTab is not null => Browser(Select(recentTab, BrowserScopeKind.RecentOwnedTaskTab)),
            ContextualSurface.DirectCapability when safeDirect is not null => new(ExecutionScopeKind.DirectCapability, Detail: "contextual_direct"),
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
