using VoiceOS.Core.Interaction;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Browser;

public interface IBrowserInteractionService
{
    ValueTask<BrowserInteractionOutcome> RunAsync(string utterance, CancellationToken cancellationToken = default,
        string? activationId = null, BrowserExecutionScope? scope = null);
    /// <summary>
    /// Starts goal normalization for an activation before the caller knows whether it will land
    /// on a browser scope at all. <see cref="RunAsync"/> consumes the result only when the same
    /// activation id and utterance are later passed in; otherwise it compiles inline.
    /// No-op by default so non-browser-capable implementations need not care.
    /// </summary>
    void PrefetchNormalization(string utterance, string activationId, CancellationToken cancellationToken = default) { }
}

public interface IBrowserActivitySource
{
    event Action<BrowserActivity>? ActionStarting;
}

public sealed record BrowserActivity(InteractionActionKind? Operation = null,
    string? Direction = null, string? Query = null, string? Objective = null,
    string? Destination = null, string? TargetRole = null, string? TargetName = null,
    bool OpeningTab = false, string? StepText = null);

public sealed class BrowserInteractionService(
    IChromeCompanionTransport transport,
    Decision.IJevGateway gateway,
    IBrowserStepCompiler? compiler = null,
    ILogger<BrowserInteractionService>? logger = null,
    IBrowserTextValueResolver? textValues = null,
    Func<nint, bool>? foregroundVerifier = null,
    double preparedFreshnessThresholdMs = 150,
    IBrowserStepRepair? repair = null) : IBrowserInteractionService, IBrowserActivitySource
{
    private readonly IBrowserStepCompiler? _compiler = compiler;
    private volatile string? _stepText;
    private static readonly InteractionBudget StepBudget = new(10, 8, 3, 12, TimeSpan.FromSeconds(50));
    public event Action<BrowserActivity>? ActionStarting;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string ActivationId, string Utterance, Task<CompiledBrowserTask?> Task, Stopwatch Clock)? _prefetch;
    private readonly double _preparedFreshnessThresholdMs = preparedFreshnessThresholdMs;

    public void PrefetchNormalization(string utterance, string activationId, CancellationToken cancellationToken = default)
    {
        if (_compiler is null) return;
        var clock = Stopwatch.StartNew();
        var task = CompileAsync(utterance, cancellationToken);
        _prefetch = (activationId, utterance, task, clock);
    }

    public async ValueTask<BrowserInteractionOutcome> RunAsync(
        string utterance, CancellationToken cancellationToken = default, string? activationId = null,
        BrowserExecutionScope? scope = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var framingTimer = Stopwatch.StartNew();
            var goal = new BrowserGoal(utterance) with
            {
                ScopedDestination = scope?.Destination,
                NamedServiceHint = scope?.NamedServiceHint
            };
            if (scope?.RequireForegroundChrome == true
                && !(foregroundVerifier ?? ForegroundMatches)(scope.ExpectedForegroundHandle))
                return new(InteractionCompletionState.Incomplete,
                    "Chrome is no longer the foreground application.", null, null, null);
            if (scope?.IsSurfaceOnly == true && scope.Kind == BrowserScopeKind.NewTaskTab
                && scope.Destination is null && ServiceResolver.Resolve(scope.NamedServiceHint) is null
                && scope.BlankTabRequested)
            {
                ActionStarting?.Invoke(new(OpeningTab: true, Destination: scope.NamedServiceHint));
                var owner = activationId ?? Guid.NewGuid().ToString("N");
                var created = await transport.CreateNewTabAsync(owner, cancellationToken).ConfigureAwait(false);
                return new(InteractionCompletionState.Complete, "Opened a new Chrome tab.", null,
                    created.Url, created.Title);
            }
            if (scope?.IsSurfaceOnly == true && scope.FocusOnly && scope.TabId is int focusTab)
            {
                ActionStarting?.Invoke(new(OpeningTab: true, Destination: scope.NamedServiceHint));
                var owner = scope.OwnerSessionId ?? activationId ?? Guid.NewGuid().ToString("N");
                await SelectPreparedTabAsync(scope, owner, focusTab, false, cancellationToken)
                    .ConfigureAwait(false);
                return new(InteractionCompletionState.Complete, "Focused the requested browser tab.",
                    null, scope.ExpectedUrl, null);
            }
            framingTimer.Stop();
            var sessionId = scope?.OwnerSessionId ?? activationId ?? Guid.NewGuid().ToString("N");

            // Known-destination NewTaskTab: the bootstrap URL cannot depend on normalization
            // (BrowserGoal.BootstrapUrl prefers ScopedDestination/NamedServiceHint over the
            // normalized service), so the task tab can start hydrating while normalization runs.
            var destinationKnownWithoutNormalization = scope?.Destination is not null
                || ServiceResolver.Resolve(scope?.NamedServiceHint) is not null;
            if (scope?.IsSurfaceOnly != true && scope?.Kind == BrowserScopeKind.NewTaskTab
                && scope.TabId is null && destinationKnownWithoutNormalization)
                return await RunKnownDestinationNewTabAsync(goal, scope!, sessionId, activationId,
                    framingTimer, cancellationToken).ConfigureAwait(false);

            // Tab-scoped runs (ActiveTab / ExistingNamedTab / RecentOwnedTaskTab): the tab is
            // fixed by scope already, so foreground verification + selection can run while
            // normalization (which does not affect which tab to select) is still in flight.
            Task<SelectionOutcome>? selectionTask = null;
            if (scope?.TabId is int overlapTab)
            {
                if (scope.RequireForegroundChrome
                    && !(foregroundVerifier ?? ForegroundMatches)(scope.ExpectedForegroundHandle))
                    return new(InteractionCompletionState.Incomplete,
                        "Chrome is no longer the foreground application.", null, null, null);
                selectionTask = RunSelectAndVerifyAsync(scope, sessionId, overlapTab, cancellationToken);
            }

            // One known page operation (Back, Forward, scroll) is executed once, directly: no compile, no decision model.
            if (scope is { PageOperation: not PageOperation.None, TabId: not null } && selectionTask is not null)
                return await RunPageOperationAsync(goal, scope, sessionId, selectionTask, cancellationToken).ConfigureAwait(false);

            var normalizationTimer = Stopwatch.StartNew();
            if (scope?.IsSurfaceOnly != true)
            {
                // One simple operation on an already-fixed surface needs no generative compile.
                var simple = SimpleStepFramer.TryFrame(goal.OriginalUtterance, scope);
                var (normalizationTask, normalizationCts) = simple is not null
                    ? (Task.FromResult<CompiledBrowserTask?>(simple), null)
                    : BeginNormalization(goal.OriginalUtterance, activationId, cancellationToken);
                if (simple is not null)
                    logger?.LogInformation("Browser compile=skipped reason=simple_step kind={Kind}", simple.Plan.Current.Kind);
                CompiledBrowserTask? compiled = null;
                if (selectionTask is not null)
                {
                    var winner = await Task.WhenAny(selectionTask, normalizationTask).ConfigureAwait(false);
                    if (winner == (Task)selectionTask)
                    {
                        var selection = await selectionTask.ConfigureAwait(false);
                        if (!selection.Success)
                        {
                            normalizationCts?.Cancel();
                            normalizationCts?.Dispose();
                            Observe(normalizationTask);
                            return new(InteractionCompletionState.Incomplete, selection.FailureDetail, null, null, null);
                        }
                    }
                }
                try
                {
                    compiled = await normalizationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (InfrastructureUnavailableException)
                {
                    if (selectionTask is not null) Observe(selectionTask);
                    throw;
                }
                catch { /* A provider failure cannot turn an ungrounded request into a literal query. */ }
                finally { normalizationCts?.Dispose(); }
                if (selectionTask is not null)
                {
                    var selection = await selectionTask.ConfigureAwait(false);
                    if (!selection.Success)
                        return new(InteractionCompletionState.Incomplete, selection.FailureDetail, null, null, null);
                }
                if (Apply(goal, scope, compiled, out var compiledGoal) is { } rejected) return rejected;
                goal = compiledGoal;
            }
            else
            {
                logger?.LogInformation("Browser normalization=skipped reason=surface_only");
                if (selectionTask is not null)
                {
                    var selection = await selectionTask.ConfigureAwait(false);
                    if (!selection.Success)
                        return new(InteractionCompletionState.Incomplete, selection.FailureDetail, null, null, null);
                }
            }
            normalizationTimer.Stop();
            LatencyTrace.Current?.Record("normalization", normalizationTimer.Elapsed.TotalMilliseconds);
            var destinationTimer = Stopwatch.StartNew();
            var destination = BrowserGoal.BootstrapUrl(goal);
            destinationTimer.Stop();
            logger?.LogInformation("Browser stage framing_ms={FramingMs:F0} normalization_ms={NormalizationMs:F0} destination_ms={DestinationMs:F0} scope={Scope} destination_origin={Destination} destination_reason={DestinationReason}",
                framingTimer.Elapsed.TotalMilliseconds,
                normalizationTimer.Elapsed.TotalMilliseconds, destinationTimer.Elapsed.TotalMilliseconds,
                scope?.Kind.ToString() ?? "NewTaskTab",
                new Uri(destination).GetLeftPart(UriPartial.Authority),
                goal.ExplicitUrl is not null ? "explicit_url"
                : ServiceResolver.Resolve(goal.NamedServiceHint) is not null ? "named_service"
                : ServiceResolver.Resolve(goal.Normalization?.PreferredService) is not null ? "normalized_service"
                : BrowserGoal.NormalizedServiceRoot(goal.Normalization) is not null ? "normalized_service_url"
                : "web_discovery");
            var decisions = new TypeSafeBrowserDecisionSource(gateway, goal, logger: logger, textValues: textValues);
            if (scope?.TabId is not null)
                ActionStarting?.Invoke(ActivityFor(goal, scope, null) with { OpeningTab = false, StepText = goal.Plan?.Current.Progress });
            var surface = CreateSurface(goal, scope, decisions, sessionId, scope?.TabId, scope?.ExpectedUrl);
            if (scope?.IsSurfaceOnly == true)
            {
                await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
                // Surface acquired is not destination acquired: a requested destination must have
                // produced web content, not a blank or internal page.
                if (scope.Destination is not null && !IsWebContent(surface.LatestSnapshot?.Url))
                    return new(InteractionCompletionState.Incomplete,
                        $"The browser did not reach {scope.NamedServiceHint ?? scope.Destination.Host}.",
                        null, surface.LatestSnapshot?.Url, surface.LatestSnapshot?.Title);
                return new(InteractionCompletionState.Complete, "The requested browser surface is ready.",
                    null, surface.LatestSnapshot?.Url, surface.LatestSnapshot?.Title);
            }
            return await RunSurfaceAsync(goal, surface, decisions, cancellationToken, scope, activationId).ConfigureAwait(false);
        }
        catch (InfrastructureUnavailableException ex)
        {
            return new(InteractionCompletionState.Incomplete, ex.Message, null, null, null, Unavailable: ex.Reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Browser task exit reason=time_budget_expired");
            return new(InteractionCompletionState.Incomplete, BrowserStepMessages.Stalled, null, null, null);
        }
        catch (ChromeCompanionException ex)
        {
            logger?.LogWarning("Browser task exit reason=companion_error code={Code}", ex.Code);
            return new(InteractionCompletionState.Incomplete, $"Chrome companion unavailable: {ex.Message}", null, null, null,
                Unavailable: ex.Code == "TRANSPORT_DISCONNECTED" ? UnavailableReason.ChromeCompanion : null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Executes exactly one history traversal or one scroll on the intended tab, observes the real result and stops.
    /// Nothing decides afterwards whether to do it again.
    /// </summary>
    private async ValueTask<BrowserInteractionOutcome> RunPageOperationAsync(BrowserGoal goal, BrowserExecutionScope scope,
        string sessionId, Task<SelectionOutcome> selectionTask, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var selection = await selectionTask.ConfigureAwait(false);
        if (!selection.Success)
            return new(InteractionCompletionState.Incomplete, selection.FailureDetail, null, null, null);
        var operation = scope.PageOperation;
        var surface = CreateSurface(goal, scope, new TypeSafeBrowserDecisionSource(gateway, goal, logger: logger),
            sessionId, scope.TabId, scope.ExpectedUrl);
        var observation = await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
        var kind = operation switch
        {
            PageOperation.Back => InteractionActionKind.GoBack,
            PageOperation.Forward => InteractionActionKind.GoForward,
            _ => InteractionActionKind.Scroll
        };
        var direction = operation == PageOperation.ScrollDown ? "down" : operation == PageOperation.ScrollUp ? "up" : null;
        var action = observation.Candidates.SelectMany(static c => c.Actions)
            .FirstOrDefault(a => a.Kind == kind && a.Direction == direction);
        if (action is null)
            return new(InteractionCompletionState.Incomplete, "I couldn't do that on this page.", null, null, null);
        logger?.LogInformation("Browser compile=skipped reason=page_operation operation={Operation}", operation);
        var scrollBefore = surface.LatestSnapshot?.Viewport.ScrollY;
        var result = await surface.ExecuteAsync(action, observation, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        LatencyTrace.Current?.Record($"page_operation_{operation.ToString().ToLowerInvariant()}", timer.Elapsed.TotalMilliseconds);
        var snapshot = surface.LatestSnapshot;
        var done = result.Succeeded && (kind != InteractionActionKind.Scroll || snapshot?.Viewport.ScrollY != scrollBefore);
        string? detail = done ? null : result.Status switch
        {
            InteractionResultStatus.StaleTarget => "The page changed before I could use that control.",
            InteractionResultStatus.TopologyAmbiguous => "The browser changed tabs unexpectedly.",
            _ when kind == InteractionActionKind.Scroll && result.Succeeded => "This page can't scroll any further.",
            _ when (result.Effects ?? []).Any(static e => e.Kind == EffectKind.NoEffect && e.Get("reason") == "NO_HISTORY")
                => kind == InteractionActionKind.GoForward ? "That tab has no next page." : "That tab has no previous page.",
            InteractionResultStatus.NoEffect => BrowserStepMessages.NoChange,
            _ => "The browser action didn't change the page."
        };
        logger?.LogInformation("Browser page operation={Operation} outcome={Outcome} detail={Detail} ms={Ms:F0}",
            operation, done ? "Complete" : "Incomplete", detail ?? "-", timer.Elapsed.TotalMilliseconds);
        var completion = done ? InteractionCompletionState.Complete : InteractionCompletionState.Incomplete;
        return new(completion, detail, null, snapshot?.Url, snapshot?.Title, 0, 1, surface.TabId, surface.SessionId,
            goal.OriginalUtterance,
            Referents: ReferentPopulator.FromBrowserRun(result.Effects, completion, surface.TabId, surface.SessionId,
                snapshot?.Url, snapshot?.Title, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Starts the task-tab bootstrap concurrently with normalization when the destination cannot
    /// change based on the normalized result. Fires ActionStarting once at the join, after
    /// normalization succeeds, so its UI message carries the normalized query — matching the
    /// non-overlapped path exactly except for when the transport call actually began.
    /// </summary>
    private async ValueTask<BrowserInteractionOutcome> RunKnownDestinationNewTabAsync(
        BrowserGoal goal, BrowserExecutionScope scope, string sessionId, string? activationId,
        Stopwatch framingTimer, CancellationToken cancellationToken)
    {
        var destination = BrowserGoal.BootstrapUrl(goal);
        var raceClock = Stopwatch.StartNew();
        double? startupDoneAtMs = null;
        LatencyTrace.Current?.MarkExternalAction();

        // Not cancelled with the caller: an abandoned transport wait would leave the extension
        // creating a tab nobody can close. The companion bounds this call with its own timeouts.
        async Task<BrowserSnapshot> TimedStartupAsync()
        {
            var snapshot = await transport.OpenTaskTabAsync(sessionId, destination, CancellationToken.None).ConfigureAwait(false);
            startupDoneAtMs = raceClock.Elapsed.TotalMilliseconds;
            return snapshot;
        }
        var startupTask = TimedStartupAsync();

        var normalizationTimer = Stopwatch.StartNew();
        var (normalizationTask, normalizationCts) = BeginNormalization(goal.OriginalUtterance, activationId, cancellationToken);
        CompiledBrowserTask? compiled = null;
        try
        {
            try
            {
                compiled = await normalizationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InfrastructureUnavailableException)
            {
                await CleanupStartupAsync(startupTask, sessionId).ConfigureAwait(false);
                throw;
            }
            catch { /* A provider failure cannot turn an ungrounded request into a literal query. */ }
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled; do not block the throw on closing a tab it no longer cares about.
            _ = CleanupStartupAsync(startupTask, sessionId);
            throw;
        }
        finally { normalizationCts?.Dispose(); }
        normalizationTimer.Stop();
        LatencyTrace.Current?.Record("normalization", normalizationTimer.Elapsed.TotalMilliseconds);

        if (Apply(goal, scope, compiled, out var compiledGoal) is { } rejected)
        {
            await CleanupStartupAsync(startupTask, sessionId).ConfigureAwait(false);
            return rejected;
        }
        goal = compiledGoal;
        var normalizationDoneAtMs = raceClock.Elapsed.TotalMilliseconds;

        // If the startup transport call throws (e.g. ChromeCompanionException), let it propagate
        // to RunAsync's outer catch — same outcome as the non-overlapped path.
        BrowserSnapshot startupSnapshot;
        try { startupSnapshot = await startupTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = CleanupStartupAsync(startupTask, sessionId);
            throw;
        }

        var destinationTimer = Stopwatch.StartNew();
        var finalDestination = BrowserGoal.BootstrapUrl(goal);
        destinationTimer.Stop();
        logger?.LogInformation("Browser stage framing_ms={FramingMs:F0} normalization_ms={NormalizationMs:F0} destination_ms={DestinationMs:F0} scope={Scope} destination_origin={Destination} destination_reason={DestinationReason}",
            framingTimer.Elapsed.TotalMilliseconds,
            normalizationTimer.Elapsed.TotalMilliseconds, destinationTimer.Elapsed.TotalMilliseconds,
            scope.Kind.ToString(),
            new Uri(finalDestination).GetLeftPart(UriPartial.Authority),
            goal.ExplicitUrl is not null ? "explicit_url"
            : ServiceResolver.Resolve(goal.NamedServiceHint) is not null ? "named_service"
            : ServiceResolver.Resolve(goal.Normalization?.PreferredService) is not null ? "normalized_service"
            : BrowserGoal.NormalizedServiceRoot(goal.Normalization) is not null ? "normalized_service_url"
            : "web_discovery");

        var decisions = new TypeSafeBrowserDecisionSource(gateway, goal, logger: logger, textValues: textValues);
        var surface = CreateSurface(goal, scope, decisions, sessionId, tabId: null, expectedFirstUrl: null);
        // The page kept hydrating while normalization ran; only reuse the startup snapshot as the
        // first observation when it finished at (or after) normalization, within a small margin.
        var reuse = startupDoneAtMs is not { } doneAt || (normalizationDoneAtMs - doneAt) <= _preparedFreshnessThresholdMs;
        surface.Prepare(startupSnapshot, reuse);
        ActionStarting?.Invoke(ActivityFor(goal, scope, null) with { StepText = goal.Plan?.Current.Progress });
        return await RunSurfaceAsync(goal, surface, decisions, cancellationToken, scope, activationId).ConfigureAwait(false);
    }

    private static bool IsWebContent(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private sealed record SelectionOutcome(bool Success, string? FailureDetail);

    private async Task<SelectionOutcome> RunSelectAndVerifyAsync(
        BrowserExecutionScope scope, string sessionId, int tabId, CancellationToken cancellationToken)
    {
        LatencyTrace.Current?.MarkExternalAction();
        var selectTimer = Stopwatch.StartNew();
        try
        {
            await SelectPreparedTabAsync(scope, sessionId, tabId,
                scope.Kind == BrowserScopeKind.ActiveTab && !scope.ExplicitSelection,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ChromeCompanionException ex)
        {
            if (ex.Code == "TRANSPORT_DISCONNECTED")
                throw new InfrastructureUnavailableException(UnavailableReason.ChromeCompanion,
                    "Chrome companion isn't connected.", ex);
            logger?.LogWarning("Browser task exit reason=companion_error code={Code}", ex.Code);
            selectTimer.Stop();
            LatencyTrace.Current?.Record("tab_select", selectTimer.Elapsed.TotalMilliseconds);
            return new(false, $"Chrome companion unavailable: {ex.Message}");
        }
        selectTimer.Stop();
        LatencyTrace.Current?.Record("tab_select", selectTimer.Elapsed.TotalMilliseconds);
        if (scope.RequireForegroundChrome
            && !(foregroundVerifier ?? ForegroundMatches)(scope.ExpectedForegroundHandle))
            return new(false, "Chrome lost foreground focus before browser observation.");
        return new(true, null);
    }

    /// <summary>
    /// Resolves the normalization source for this run: a matching prefetch started by the
    /// orchestrator before scope resolution, or a fresh call under a cancellable linked token.
    /// </summary>
    private (Task<CompiledBrowserTask?> Task, CancellationTokenSource? Cts) BeginNormalization(
        string utterance, string? activationId, CancellationToken cancellationToken)
    {
        var prefetch = _prefetch;
        if (prefetch is { } p && activationId is not null && p.ActivationId == activationId
            && string.Equals(p.Utterance, utterance, StringComparison.Ordinal))
        {
            _prefetch = null;
            logger?.LogInformation("Browser normalization prefetch=hit");
            return (ObservePrefetchAsync(p.Task, p.Clock), null);
        }
        logger?.LogInformation("Browser normalization prefetch=miss");
        if (_compiler is null)
            return (Task.FromResult<CompiledBrowserTask?>(null), null);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return (CompileAsync(utterance, cts.Token), cts);
    }

    private async Task<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try { return await _compiler!.CompileAsync(utterance, token).ConfigureAwait(false); }
        finally { logger?.LogInformation("Browser compile_ms={Ms:F0}", timer.Elapsed.TotalMilliseconds); }
    }

    /// <summary>
    /// Attaches a compiled task to the goal, or returns the outcome that ends the run. A compile that produced
    /// nothing usable is a failure with a specific message, not a question for the user.
    /// </summary>
    private BrowserInteractionOutcome? Apply(BrowserGoal goal, BrowserExecutionScope? scope,
        CompiledBrowserTask? compiled, out BrowserGoal applied)
    {
        applied = goal;
        if (compiled is null)
        {
            logger?.LogWarning("Browser normalization=failed");
            return new(InteractionCompletionState.Incomplete, "I couldn't work out the steps for that request.",
                null, null, null);
        }
        if (scope?.GoalShape == GoalShape.ActionOnSurface && compiled.Normalization?.EndState == SemanticEndState.SurfaceReady)
            return new(InteractionCompletionState.Incomplete,
                "The request needs an action, but I only found a site to open.", null, null, null);
        // Only a destination the user named may constrain the task; a model's suggestion is dropped here, once.
        compiled = DestinationGrounding.Ground(compiled, goal.OriginalUtterance,
            destinationKnown: scope?.Destination is not null || ServiceResolver.Resolve(scope?.NamedServiceHint) is not null
                || goal.ExplicitUrl is not null);
        applied = goal with { Normalization = compiled.Normalization, Plan = compiled.Plan };
        if (compiled.Normalization is { } normalized)
            logger?.LogInformation("Browser normalization=used reason={Reason} objective={Objective} end_state={EndState} resource_type={ResourceType} service={Service} entity={Entity} queries={Queries} corrections={Corrections} steps={Steps}",
                "semantic_goal", normalized.Objective, normalized.EndState, normalized.ResourceType, normalized.PreferredService,
                normalized.Entity, string.Join(" | ", normalized.SearchQueries),
                string.Join(" | ", normalized.CorrectedTerms.Select(x => $"{x.Heard}->{x.Interpreted} ({x.Confidence:F2})")),
                string.Join(" > ", compiled.Plan.Steps.Select(x => $"{x.Kind}:{x.Description}")));
        return null;
    }

    private async Task<CompiledBrowserTask?> ObservePrefetchAsync(
        Task<CompiledBrowserTask?> task, Stopwatch clock)
    {
        try { return await task.ConfigureAwait(false); }
        finally
        {
            logger?.LogInformation("Browser normalization prefetch_duration_ms={Ms:F0}", clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Best-effort: close a task tab a run started but never used, without leaking it.</summary>
    private async Task CleanupStartupAsync(Task<BrowserSnapshot> startupTask, string sessionId)
    {
        BrowserSnapshot? snapshot = null;
        try { snapshot = await startupTask.ConfigureAwait(false); }
        catch { /* Startup itself failed or was cancelled; nothing was opened to close. */ }
        if (snapshot is null) return;
        try
        {
            await transport.CloseTaskTabAsync(sessionId, snapshot.TabId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Browser cleanup close_task_tab failed tab={TabId}", snapshot.TabId);
        }
    }

    private static void Observe(Task task) => task.ContinueWith(
        static t => _ = t.Exception, TaskScheduler.Default);

    private static BrowserActivity ActivityFor(BrowserGoal goal, BrowserExecutionScope? scope, InteractionAction? action)
    {
        var destinationName = ServiceResolver.Resolve(goal.NamedServiceHint)
            ?? ServiceResolver.Resolve(goal.Normalization?.PreferredService);
        var query = action?.Text is { } text && goal.Normalization?.SearchQueries
            .Any(q => string.Equals(q, text, StringComparison.OrdinalIgnoreCase)) == true
            ? text : null;
        if (action is null)
            query = goal.Normalization?.SearchQueries.FirstOrDefault();
        return new(action?.Kind, action?.Direction, query,
            goal.Normalization?.Objective, destinationName?.CanonicalName
                ?? scope?.Destination?.Host,
            OpeningTab: action is null);
    }

    private BrowserSurface CreateSurface(BrowserGoal goal, BrowserExecutionScope? scope,
        TypeSafeBrowserDecisionSource decisions, string sessionId, int? tabId, string? expectedFirstUrl)
    {
        BrowserSurface? surface = null;
        surface = new BrowserSurface(transport, goal, decisions, sessionId: sessionId,
            logger: logger, tabId: tabId, expectedFirstUrl: expectedFirstUrl,
            onActionStarting: action =>
            {
                var element = action?.TargetId is { } id
                    ? surface?.LatestSnapshot?.Elements.FirstOrDefault(e => e.Ref == id) : null;
                ActionStarting?.Invoke(ActivityFor(goal, scope, action) with
                {
                    TargetRole = element?.Role,
                    TargetName = element?.Name,
                    StepText = _stepText
                });
            });
        return surface;
    }

    private ValueTask<BrowserInteractionOutcome> RunSurfaceAsync(
        BrowserGoal goal, BrowserSurface surface, TypeSafeBrowserDecisionSource decisions,
        CancellationToken cancellationToken, BrowserExecutionScope? scope = null, string? turnId = null)
        => goal.Plan is null
            ? ValueTask.FromResult(new BrowserInteractionOutcome(InteractionCompletionState.Incomplete,
                BrowserStepMessages.NotUnderstood, null, null, null))
            : RunPlanAsync(goal, surface, decisions, cancellationToken, scope, turnId);

    /// <summary>
    /// Runs the compiled plan one semantic step at a time. Each step is decided against only itself, observed in code,
    /// and completed by its own postcondition; the next step starts from the state the previous one ended in.
    /// </summary>
    private async ValueTask<BrowserInteractionOutcome> RunPlanAsync(
        BrowserGoal goal, BrowserSurface surface, TypeSafeBrowserDecisionSource decisions,
        CancellationToken cancellationToken, BrowserExecutionScope? scope, string? turnId)
    {
        var plan = goal.Plan!;
        var engine = new InteractionEngine(new PlannedStepEvaluator(goal), ProofMode.On);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(120));
        InteractionObservation? carry = null;
        var effects = new List<Effect>();
        int decisionsTotal = 0, actionsTotal = 0;
        InteractionRunResult? last = null;
        string? lastRepairReason = null;
        try
        {
            while (true)
            {
                var step = plan.Current;
                decisions.Plan = plan;
                _stepText = step.Progress;
                if (plan.CurrentStepIndex > 0 || step.Progress is not null)
                    ActionStarting?.Invoke(new(StepText: step.Progress));
                var stepTimer = Stopwatch.StartNew();
                InteractionRunResult result;
                (result, plan, var repairReason) = await RunStepAsync(engine, plan, goal, scope, turnId ?? surface.SessionId,
                    surface, decisions, carry, overall.Token).ConfigureAwait(false);
                step = plan.Current;
                lastRepairReason = repairReason;
                var framed = PlanFraming.Frame(plan, goal, scope, turnId ?? surface.SessionId);
                logger?.LogInformation("Browser step index={Index}/{Count} kind={Kind} family={Family} outcome={Outcome} decisions={Decisions} actions={Actions} step_ms={Ms:F0} reason={Reason} rule={Rule}",
                    plan.CurrentStepIndex + 1, plan.Steps.Count, step.Kind, framed.Family, result.Completion, result.Progress.Decisions,
                    result.Progress.Actions, stepTimer.Elapsed.TotalMilliseconds, result.ReasonCode ?? "-", result.Detail);
                LatencyTrace.Current?.Record($"step_{plan.CurrentStepIndex + 1}_{step.Kind.ToString().ToLowerInvariant()}",
                    stepTimer.Elapsed.TotalMilliseconds);
                decisionsTotal += result.Progress.Decisions;
                actionsTotal += result.Progress.Actions;
                effects.AddRange(result.Effects ?? []);
                last = result;
                carry = result.Observation.Revision == surface.CurrentRevision ? result.Observation : null;
                if (result.Completion != InteractionCompletionState.Complete) break;
                if (plan.IsLastStep)
                {
                    if (scope is { Kind: BrowserScopeKind.ActiveTab, ExplicitSelection: false } && actionsTotal == 0
                        && step.Kind == PlanStepKind.Act)
                        last = result with { Completion = InteractionCompletionState.Incomplete,
                            Detail = "Nothing was done on the current page yet.", ReasonCode = "no_action" };
                    break;
                }
                plan = plan.Advance();
            }
        }
        finally { _stepText = null; }
        foreach (var effect in effects)
            logger?.LogInformation("Browser effect id={EffectId} {Effect}", effect.Id, effect.Summarize());
        var final = last!;
        // Only a question the user can settle stays a question; every other uncertainty is a specific failure.
        if (final.Completion == InteractionCompletionState.Uncertain && final.ReasonCode != "needs_user")
            final = final with { Completion = InteractionCompletionState.Incomplete };
        var userDetail = final.Completion switch
        {
            InteractionCompletionState.Complete => null,
            InteractionCompletionState.Uncertain => final.Detail,
            _ => final.Detail is "Nothing was done on the current page yet." ? final.Detail
                : BrowserStepMessages.Failure(plan, final.ReasonCode, final.Detail, final.Observation, lastRepairReason)
        };
        logger?.LogInformation("Browser task outcome={Outcome} resumable={Resumable} detail={Detail} decisions={Decisions} actions={Actions} steps_done={Done}/{Total} origin={Origin}",
            final.Completion, false, final.Detail, decisionsTotal, actionsTotal,
            plan.CurrentStepIndex + (final.Completion == InteractionCompletionState.Complete ? 1 : 0), goal.Plan!.Steps.Count,
            BrowserSurface.LogOrigin(surface.LatestSnapshot?.Url));
        return new(final.Completion, userDetail, final.Choices,
            surface.LatestSnapshot?.Url, surface.LatestSnapshot?.Title, decisionsTotal, actionsTotal, surface.TabId, surface.SessionId,
            goal.Normalization?.Objective ?? plan.FinalGoal,
            Referents: ReferentPopulator.FromBrowserRun(effects, final.Completion, surface.TabId,
                surface.SessionId, surface.LatestSnapshot?.Url, surface.LatestSnapshot?.Title, DateTimeOffset.UtcNow));
    }

    private static readonly HashSet<string> RecoverableCodes = new(StringComparer.Ordinal)
    {
        "low_operation_confidence", "unoffered_operation", "invalid_target", "repeated_action", "blocked",
        "unconfirmed_completion", "unestablished_completion", "unconfirmed_block", "text_value_unresolved",
        "text_value_error", "correction_exhausted", "completion_unconfirmed", "repeated_failure", "no_action"
    };

    private static bool Recoverable(InteractionRunResult result)
        => result.Completion != InteractionCompletionState.Complete && result.ReasonCode is { } code && RecoverableCodes.Contains(code);

    /// <summary>Returns the one offered history traversal, once. A second request means the first had no effect.</summary>
    private sealed class HistoryDecisions(string? direction) : IInteractionDecisionSource
    {
        private int _used;
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken cancellationToken = default)
        {
            var kind = string.Equals(direction, "forward", StringComparison.OrdinalIgnoreCase)
                ? InteractionActionKind.GoForward : InteractionActionKind.GoBack;
            var action = context.Observation.Candidates.SelectMany(static c => c.Actions).FirstOrDefault(a => a.Kind == kind);
            return ValueTask.FromResult(Interlocked.Exchange(ref _used, 1) == 0 && action is not null
                ? InteractionDecision.Act(action)
                : InteractionDecision.Unsure(BrowserStepMessages.NoChange, reasonCode: "no_action"));
        }
    }

    /// <summary>A decision source that first returns one fixed decision (the repaired action), then defers.</summary>
    private sealed class OneShotDecisions(InteractionDecision first, IInteractionDecisionSource inner) : IInteractionDecisionSource
    {
        private int _used;
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _used, 1) == 0 ? ValueTask.FromResult(first) : inner.DecideAsync(context, cancellationToken);
    }

    /// <summary>
    /// Runs one step. A step the cheap decision cannot settle is recovered internally, at most once: re-evaluate
    /// against a fresh observation if the page moved, then one focused semantic repair. Only a repair that finds the
    /// request itself ambiguous can turn into a question for the user; everything else ends as a specific failure.
    /// </summary>
    private async ValueTask<(InteractionRunResult Result, InteractionPlan Plan, string? RepairReason)> RunStepAsync(
        InteractionEngine engine, InteractionPlan plan, BrowserGoal goal, BrowserExecutionScope? scope, string turnId,
        BrowserSurface surface, TypeSafeBrowserDecisionSource decisions, InteractionObservation? carry,
        CancellationToken cancellationToken)
    {
        OutcomeStep Framed(InteractionPlan p) => PlanFraming.Frame(p, goal, scope, turnId);
        Task<InteractionRunResult> Run(InteractionPlan p, IInteractionDecisionSource source, InteractionObservation? start)
        {
            decisions.Plan = p;
            return engine.RunAsync(new(p.Current.Description, Framed(p)), surface, source, StepBudget, cancellationToken, start)
                .AsTask();
        }

        // A history step is one traversal: a fixed decision, never recovered, never repeated.
        if (plan.Current.Kind == PlanStepKind.History)
            return (await Run(plan, new HistoryDecisions(plan.Current.Target), carry).ConfigureAwait(false), plan, null);
        var result = await Run(plan, decisions, carry).ConfigureAwait(false);
        if (!Recoverable(result)) return (result, plan, null);

        var recoveryTimer = Stopwatch.StartNew();
        logger?.LogInformation("Browser recovery start step={Step} code={Code}", plan.CurrentStepIndex + 1, result.ReasonCode);
        // 1. The page may have moved while the decision was uncertain: judge the same step again on what is there now.
        var fresh = await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(fresh.StateKey, result.Observation.StateKey))
        {
            var again = await Run(plan, decisions, fresh).ConfigureAwait(false);
            if (!Recoverable(again))
            {
                logger?.LogInformation("Browser recovery outcome=reevaluated completion={Completion} recovery_ms={Ms:F0}",
                    again.Completion, recoveryTimer.Elapsed.TotalMilliseconds);
                return (again, plan, null);
            }
            result = again;
            fresh = again.Observation.Revision == surface.CurrentRevision
                ? again.Observation : await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. One bounded semantic repair.
        StepRepairResult? fix = null;
        if (repair is not null)
        {
            fix = await repair.RepairAsync(new(plan, result.ReasonCode!, result.Detail, fresh, result.RecentHistory,
                result.Effects ?? []), cancellationToken).ConfigureAwait(false);
            LatencyTrace.Current?.Record("repair", recoveryTimer.Elapsed.TotalMilliseconds);
        }
        logger?.LogInformation("Browser recovery repair={Kind} recovery_ms={Ms:F0}", fix?.Kind.ToString() ?? "unavailable",
            recoveryTimer.Elapsed.TotalMilliseconds);
        var step = plan.Current;
        switch (fix?.Kind)
        {
            case StepRepairKind.Bind when BindRepair(fix, step, fresh) is { } chosen:
                return (await Run(plan, new OneShotDecisions(chosen, decisions), fresh).ConfigureAwait(false), plan, null);
            case StepRepairKind.Reframe when !string.IsNullOrWhiteSpace(fix.NewDescription):
                var reframed = plan.WithCurrent(step with { Description = fix.NewDescription!.Trim() });
                return (await Run(reframed, decisions, fresh).ConfigureAwait(false), reframed, null);
            case StepRepairKind.NeedsUser when !string.IsNullOrWhiteSpace(fix.Question) && fix.Options is { Count: >= 2 }:
                return (result with
                {
                    Completion = InteractionCompletionState.Uncertain, Detail = fix.Question, ReasonCode = "needs_user",
                    Choices = fix.Options.Select((o, i) => new InteractionChoice($"option:{i}", o))
                        .Append(new("cancel", "Cancel")).ToArray()
                }, plan, null);
            case StepRepairKind.Impossible:
                return (result with { Completion = InteractionCompletionState.Incomplete, ReasonCode = "impossible" }, plan, fix.Reason);
            default:
                return (result with { Completion = InteractionCompletionState.Incomplete }, plan, null);
        }
    }

    /// <summary>The repaired action, only if it is one the fresh observation actually offers.</summary>
    private static InteractionDecision? BindRepair(StepRepairResult fix, PlanStep step, InteractionObservation fresh)
    {
        var kind = fix.Operation switch
        {
            "click" => InteractionActionKind.Activate, "type" => InteractionActionKind.SetText,
            "back" => InteractionActionKind.GoBack, "scroll_down" or "scroll_up" => InteractionActionKind.Scroll, _ => (InteractionActionKind?)null
        };
        var candidate = fresh.Candidates.FirstOrDefault(c => c.Id == fix.CandidateId)
            ?? (kind is InteractionActionKind.GoBack or InteractionActionKind.Scroll
                ? fresh.Candidates.FirstOrDefault(c => c.Actions.Any(a => a.Kind == kind)) : null);
        if (kind is null || candidate is null) return null;
        // Locating only reveals content; clicking a control would navigate away from the results being searched.
        if (step.Kind == PlanStepKind.Locate && kind != InteractionActionKind.Scroll) return null;
        var direction = fix.Operation == "scroll_up" ? "up" : fix.Operation == "scroll_down" ? "down" : null;
        var offered = candidate.Actions.FirstOrDefault(a => a.Kind == kind && (direction is null || a.Direction == direction));
        if (offered is null) return null;
        if (kind == InteractionActionKind.SetText)
        {
            // A search step only ever types its own grounded query.
            var text = step.Kind == PlanStepKind.Search ? step.Query : fix.Text;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 240 || text.Any(char.IsControl)) return null;
            offered = offered with { Text = text.Trim() };
        }
        // A deliberately chosen control is a binding: the Open postcondition then decides from its observed effect.
        TargetBinding? binding = kind == InteractionActionKind.Activate && step.Kind == PlanStepKind.Open
            ? new(candidate.Id, fresh.Revision, BindingMethod.JevChoice,
                fresh.Candidates.Count(c => c.Actions.Any(a => a.Kind == InteractionActionKind.Activate)), .9, .5, candidate.Label)
            : null;
        return InteractionDecision.Act(offered) with { TargetBinding = binding };
    }

    private async ValueTask SelectPreparedTabAsync(BrowserExecutionScope scope, string sessionId,
        int tabId, bool requireActive, CancellationToken cancellationToken)
    {
        if (scope.ExpectedTitle is { } title)
        {
            try
            {
                await transport.SelectTabWithTitleAsync(sessionId, tabId, scope.ExpectedUrl!, title,
                    requireActive, cancellationToken).ConfigureAwait(false);
                logger?.LogInformation("Named tab candidate_id=tab_{TabId} atomic_url_title_verification=passed", tabId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("Named tab candidate_id=tab_{TabId} atomic_url_title_verification=failed reason={Reason}",
                    tabId, ex is ChromeCompanionException companion ? companion.Code : ex.GetType().Name);
                throw;
            }
            return;
        }
        await transport.SelectTabAsync(sessionId, tabId, scope.ExpectedUrl!, requireActive,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool ForegroundMatches(nint expectedHandle)
        => CandidateBuilder.IsForegroundProcessWindow(expectedHandle, "chrome");
}
