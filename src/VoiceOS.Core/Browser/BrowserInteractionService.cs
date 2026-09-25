using VoiceOS.Core.Interaction;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Browser;

public interface IBrowserInteractionService
{
    ValueTask<BrowserInteractionOutcome> RunAsync(string utterance, CancellationToken cancellationToken = default,
        string? activationId = null, BrowserExecutionScope? scope = null);
    ValueTask<BrowserInteractionOutcome> ResumeAsync(string choiceId, CancellationToken cancellationToken = default);
}

public sealed class BrowserInteractionService(
    IChromeCompanionTransport transport,
    Decision.IJevGateway gateway,
    IBrowserGoalNormalizer? normalizer = null,
    ILogger<BrowserInteractionService>? logger = null,
    IBrowserTextValueResolver? textValues = null,
    Func<nint, bool>? foregroundVerifier = null) : IBrowserInteractionService
{
    private readonly InteractionEngine _engine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingRun? _pending;

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
                var owner = activationId ?? Guid.NewGuid().ToString("N");
                var created = await transport.CreateNewTabAsync(owner, cancellationToken).ConfigureAwait(false);
                return new(InteractionCompletionState.Complete, "Opened a new Chrome tab.", null,
                    created.Url, created.Title);
            }
            if (scope?.IsSurfaceOnly == true && scope.FocusOnly && scope.TabId is int focusTab)
            {
                var owner = scope.OwnerSessionId ?? activationId ?? Guid.NewGuid().ToString("N");
                await SelectPreparedTabAsync(scope, owner, focusTab, false, cancellationToken)
                    .ConfigureAwait(false);
                return new(InteractionCompletionState.Complete, "Focused the requested browser tab.",
                    null, scope.ExpectedUrl, null);
            }
            framingTimer.Stop();
            var normalizationTimer = Stopwatch.StartNew();
            if (scope?.IsSurfaceOnly != true)
            {
                BrowserGoalNormalization? normalized = null;
                try
                {
                    if (normalizer is not null)
                        normalized = await normalizer.NormalizeAsync(goal.OriginalUtterance, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* A provider failure cannot turn an ungrounded request into a literal query. */ }
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
                logger?.LogInformation("Browser normalization=used reason={Reason} objective={Objective} service={Service} entity={Entity} queries={Queries} corrections={Corrections}",
                    "semantic_goal", normalized.Objective, normalized.PreferredService,
                    normalized.Entity, string.Join(" | ", normalized.SearchQueries),
                    string.Join(" | ", normalized.CorrectedTerms.Select(x => $"{x.Heard}->{x.Interpreted} ({x.Confidence:F2})")));
            }
            else logger?.LogInformation("Browser normalization=skipped reason=surface_only");
            normalizationTimer.Stop();
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
                : "web_discovery");
            var decisions = new TypeSafeBrowserDecisionSource(gateway, goal, logger: logger,
                textValues: textValues);
            var sessionId = scope?.OwnerSessionId ?? activationId ?? Guid.NewGuid().ToString("N");
            if (scope?.TabId is int selectedTab)
            {
                if (scope.RequireForegroundChrome
                    && !(foregroundVerifier ?? ForegroundMatches)(scope.ExpectedForegroundHandle))
                    return new(InteractionCompletionState.Incomplete,
                        "Chrome is no longer the foreground application.", null, null, null);
                await SelectPreparedTabAsync(scope, sessionId, selectedTab,
                    scope.Kind == BrowserScopeKind.ActiveTab && !scope.ExplicitSelection,
                    cancellationToken).ConfigureAwait(false);
                if (scope.RequireForegroundChrome
                    && !(foregroundVerifier ?? ForegroundMatches)(scope.ExpectedForegroundHandle))
                    return new(InteractionCompletionState.Incomplete,
                        "Chrome lost foreground focus before browser observation.", null, null, null);
            }
            var surface = new BrowserSurface(transport, goal, decisions, sessionId: sessionId,
                logger: logger, tabId: scope?.TabId, expectedFirstUrl: scope?.ExpectedUrl);
            if (scope?.IsSurfaceOnly == true)
            {
                var prepared = await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
                _ = prepared;
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
        => expectedHandle != 0 && CandidateBuilder.GetOpenWindows().Any(window =>
            window.Hwnd == expectedHandle && window.IsForeground
            && window.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase));
}
