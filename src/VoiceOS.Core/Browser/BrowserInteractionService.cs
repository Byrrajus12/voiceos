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
    ValueTask<BrowserInteractionOutcome> ResumeAsync(string choiceId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Starts goal normalization for an activation before the caller knows whether it will land
    /// on a browser scope at all. <see cref="RunAsync"/> consumes the result only when the same
    /// activation id and utterance are later passed in; otherwise it normalizes inline as before.
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
    bool OpeningTab = false);

public sealed class BrowserInteractionService(
    IChromeCompanionTransport transport,
    Decision.IJevGateway gateway,
    IBrowserGoalNormalizer? normalizer = null,
    ILogger<BrowserInteractionService>? logger = null,
    IBrowserTextValueResolver? textValues = null,
    Func<nint, bool>? foregroundVerifier = null,
    double preparedFreshnessThresholdMs = 150) : IBrowserInteractionService, IBrowserActivitySource
{
    public event Action<BrowserActivity>? ActionStarting;
    private readonly InteractionEngine _engine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingRun? _pending;
    private (string ActivationId, string Utterance, Task<BrowserGoalNormalization?> Task, Stopwatch Clock)? _prefetch;
    private readonly double _preparedFreshnessThresholdMs = preparedFreshnessThresholdMs;

    public void PrefetchNormalization(string utterance, string activationId, CancellationToken cancellationToken = default)
    {
        if (normalizer is null) return;
        var clock = Stopwatch.StartNew();
        var task = normalizer.NormalizeAsync(utterance, cancellationToken).AsTask();
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
                && scope.Destination is null)
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

            var normalizationTimer = Stopwatch.StartNew();
            if (scope?.IsSurfaceOnly != true)
            {
                var (normalizationTask, normalizationCts) = BeginNormalization(
                    goal.OriginalUtterance, activationId, cancellationToken);
                BrowserGoalNormalization? normalized = null;
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
                    normalized = await normalizationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* A provider failure cannot turn an ungrounded request into a literal query. */ }
                finally { normalizationCts?.Dispose(); }
                if (selectionTask is not null)
                {
                    var selection = await selectionTask.ConfigureAwait(false);
                    if (!selection.Success)
                        return new(InteractionCompletionState.Incomplete, selection.FailureDetail, null, null, null);
                }
                if (normalized is null)
                {
                    logger?.LogWarning("Browser normalization=failed");
                    return new(InteractionCompletionState.Uncertain,
                        "I could not safely clarify the browser goal. Please rephrase the destination and what to find.",
                        [new("cancel", "Cancel")], null, null);
                }
                if (scope?.GoalShape == GoalShape.ActionOnSurface
                    && normalized.EndState == SemanticEndState.SurfaceReady)
                    return new(InteractionCompletionState.Uncertain,
                        "The semantic goal still requires an action, but normalization identified only a surface.",
                        [new("cancel", "Cancel")], null, null);
                goal = goal with { Normalization = normalized };
                logger?.LogInformation("Browser normalization=used reason={Reason} objective={Objective} end_state={EndState} resource_type={ResourceType} service={Service} entity={Entity} queries={Queries} corrections={Corrections}",
                    "semantic_goal", normalized.Objective, normalized.EndState, normalized.ResourceType, normalized.PreferredService,
                    normalized.Entity, string.Join(" | ", normalized.SearchQueries),
                    string.Join(" | ", normalized.CorrectedTerms.Select(x => $"{x.Heard}->{x.Interpreted} ({x.Confidence:F2})")));
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
            var decisions = new TypeSafeBrowserDecisionSource(gateway, goal, logger: logger,
                textValues: textValues);
            if (scope?.TabId is not null)
                ActionStarting?.Invoke(ActivityFor(goal, scope, null));
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
            return await RunSurfaceAsync(goal, surface, decisions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Browser task exit reason=time_budget_expired");
            return new(InteractionCompletionState.Incomplete, "The browser interaction time budget expired.", null, null, null);
        }
        catch (ChromeCompanionException ex)
        {
            logger?.LogWarning("Browser task exit reason=companion_error code={Code}", ex.Code);
            return new(InteractionCompletionState.Incomplete, $"Chrome companion unavailable: {ex.Message}", null, null, null);
        }
        finally { _gate.Release(); }
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
        BrowserGoalNormalization? normalized = null;
        try
        {
            try
            {
                normalized = await normalizationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

        if (normalized is null)
        {
            logger?.LogWarning("Browser normalization=failed");
            await CleanupStartupAsync(startupTask, sessionId).ConfigureAwait(false);
            return new(InteractionCompletionState.Uncertain,
                "I could not safely clarify the browser goal. Please rephrase the destination and what to find.",
                [new("cancel", "Cancel")], null, null);
        }
        if (scope.GoalShape == GoalShape.ActionOnSurface && normalized.EndState == SemanticEndState.SurfaceReady)
        {
            await CleanupStartupAsync(startupTask, sessionId).ConfigureAwait(false);
            return new(InteractionCompletionState.Uncertain,
                "The semantic goal still requires an action, but normalization identified only a surface.",
                [new("cancel", "Cancel")], null, null);
        }
        goal = goal with { Normalization = normalized };
        logger?.LogInformation("Browser normalization=used reason={Reason} objective={Objective} end_state={EndState} resource_type={ResourceType} service={Service} entity={Entity} queries={Queries} corrections={Corrections}",
            "semantic_goal", normalized.Objective, normalized.EndState, normalized.ResourceType, normalized.PreferredService,
            normalized.Entity, string.Join(" | ", normalized.SearchQueries),
            string.Join(" | ", normalized.CorrectedTerms.Select(x => $"{x.Heard}->{x.Interpreted} ({x.Confidence:F2})")));
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
        ActionStarting?.Invoke(ActivityFor(goal, scope, null));
        return await RunSurfaceAsync(goal, surface, decisions, cancellationToken).ConfigureAwait(false);
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
    private (Task<BrowserGoalNormalization?> Task, CancellationTokenSource? Cts) BeginNormalization(
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
        if (normalizer is null)
            return (Task.FromResult<BrowserGoalNormalization?>(null), null);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return (normalizer.NormalizeAsync(utterance, cts.Token).AsTask(), cts);
    }

    private async Task<BrowserGoalNormalization?> ObservePrefetchAsync(
        Task<BrowserGoalNormalization?> task, Stopwatch clock)
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
                    TargetName = element?.Name
                });
            });
        return surface;
    }

    public async ValueTask<BrowserInteractionOutcome> ResumeAsync(
        string choiceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is null)
                return new(InteractionCompletionState.Incomplete, "There is no suspended browser interaction.", null, null, null);
            if (choiceId == "cancel")
            {
                _pending = null;
                return new(InteractionCompletionState.Incomplete, "Browser interaction cancelled.", null, null, null);
            }
            if (_pending.Surface.TabId is int ownedTabId)
                await transport.FocusTaskTabAsync(_pending.Surface.SessionId, ownedTabId, cancellationToken).ConfigureAwait(false);
            if (choiceId == "complete")
            {
                var accepted = _pending.Surface.LatestSnapshot;
                _pending = null;
                return new(InteractionCompletionState.Complete, "The user accepted the current result.", null,
                    accepted?.Url, accepted?.Title);
            }
            if (choiceId.StartsWith("browser:", StringComparison.Ordinal))
            {
                var targetId = choiceId.Split(':').LastOrDefault();
                var candidate = _pending.Result.Observation.Candidates.FirstOrDefault(item => item.Id == targetId);
                var action = candidate?.Actions.FirstOrDefault(static item => item.Kind == InteractionActionKind.Activate);
                if (action is null)
                    return Current("The selected browser choice is no longer available.");
                var execution = await _pending.Surface.ExecuteAsync(action, _pending.Result.Observation, cancellationToken).ConfigureAwait(false);
                if (!execution.Succeeded)
                    return Current(execution.Detail);
            }
            else if (choiceId != "continue")
                return Current("Unknown browser choice.");

            var pending = _pending;
            return await RunSurfaceAsync(pending.Goal, pending.Surface, pending.Decisions, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        BrowserInteractionOutcome Current(string? detail)
            => new(InteractionCompletionState.Incomplete, detail, null,
                _pending?.Surface.LatestSnapshot?.Url, _pending?.Surface.LatestSnapshot?.Title);
    }

    private async ValueTask<BrowserInteractionOutcome> RunSurfaceAsync(
        BrowserGoal goal, BrowserSurface surface, TypeSafeBrowserDecisionSource decisions,
        CancellationToken cancellationToken)
    {
        var result = await _engine.RunAsync(new(goal.OriginalUtterance), surface, decisions,
            new InteractionBudget(30, 24, 3, 30, TimeSpan.FromSeconds(90)), cancellationToken).ConfigureAwait(false);
        _pending = result.Completion == InteractionCompletionState.Uncertain
            ? new(goal, surface, decisions, result) : null;
        logger?.LogInformation("Browser task outcome={Outcome} resumable={Resumable} detail={Detail} decisions={Decisions} actions={Actions} origin={Origin}",
            result.Completion, _pending is not null, result.Detail,
            result.Progress.Decisions, result.Progress.Actions, BrowserSurface.LogOrigin(surface.LatestSnapshot?.Url));
        return new(result.Completion, result.Detail, result.Choices,
            surface.LatestSnapshot?.Url, surface.LatestSnapshot?.Title,
            result.Progress.Decisions, result.Progress.Actions, surface.TabId, surface.SessionId,
            goal.Normalization?.Objective);
    }

    private sealed record PendingRun(BrowserGoal Goal, BrowserSurface Surface,
        TypeSafeBrowserDecisionSource Decisions, InteractionRunResult Result);

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
