namespace VoiceOS.Core.Browser;

/// <summary>
/// The classifier supplies semantic evidence; code enforces consistency between its independent heads so one
/// fuzzy answer cannot override an obvious pragmatic fact.
/// </summary>
public static class RouteInvariants
{
    public static CommandRouteDecision Apply(CommandRouteDecision route)
    {
        var operation = route.PageOperation;
        if (operation == PageOperation.None || !route.IntentActionable && route.Reason != RoutingReason.UnresolvedReturn)
            return route with { PageOperation = PageOperation.None };

        if (operation is PageOperation.Back or PageOperation.Forward)
        {
            // Media playback keeps its own "previous"; history is only ever navigation.
            if (route.ReturnTarget == ReturnTarget.NavigationHistory)
                return OnCurrentSurface(route, operation);
            // An unresolved return is settled by the scope resolver, which owns the visible browser surface.
            if (route.ReturnTarget == ReturnTarget.Uncertain && route.Reason == RoutingReason.UnresolvedReturn && operation == PageOperation.Back)
                return route;
            return route with { PageOperation = PageOperation.None };
        }
        // A scroll acts on the page in view, whatever the independent relation/dependency heads said.
        return route.MediaRequestKind == MediaRequestKind.ContentSelection
            ? route with { PageOperation = PageOperation.None } : OnCurrentSurface(route, operation);
    }

    private static CommandRouteDecision OnCurrentSurface(CommandRouteDecision route, PageOperation operation) => route with
    {
        Route = CommandRoute.ComputerUse, Reason = RoutingReason.None, PageOperation = operation,
        ContextDependency = ContextDependency.RequiresCurrentSurface, GoalShape = GoalShape.ActionOnSurface,
        MediaOperation = null, Detail = "One page operation on the current surface."
    };
}
