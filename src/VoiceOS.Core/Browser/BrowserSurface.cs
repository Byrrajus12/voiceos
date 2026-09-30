using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceOS.Core.Interaction;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Browser;

public interface IBrowserCompletionEvaluator
{
    ValueTask<InteractionCompletionAssessment> AssessAsync(
        BrowserGoal goal, InteractionObservation observation,
        IReadOnlyList<InteractionHistoryEntry> recentHistory,
        CancellationToken cancellationToken = default);
}

/// <summary>Interaction surface backed exclusively by the normal-Chrome companion.</summary>
public sealed class BrowserSurface : IInteractionSurface
{
    private readonly IChromeCompanionTransport _transport;
    private readonly ILogger? _logger;
    private readonly BrowserGoal _goal;
    private readonly IBrowserCompletionEvaluator _completion;
    private readonly string _sessionId;
    private readonly Action<InteractionAction?>? _onActionStarting;
    private readonly Dictionary<long, BrowserSnapshot> _snapshots = [];
    private int? _tabId;
    private string? _expectedFirstUrl;
    private long _revision;
    private BrowserSnapshot? _actionSnapshot;
    private BrowserSnapshot? _preparedSnapshot;
    // Acquisition facts the next accepted observation reports exactly once.
    private (string Mode, int? FromTabId)? _pendingAcquisition;
    // Evidence already built for the last action snapshot so Accept does not serialize it twice.
    private (BrowserSnapshot Snapshot, string Evidence, string StateKey)? _actionEvidence;

    public BrowserSurface(IChromeCompanionTransport transport, BrowserGoal goal,
        IBrowserCompletionEvaluator completion, string? sessionId = null, ILogger? logger = null,
        int? tabId = null, string? expectedFirstUrl = null,
        Action<InteractionAction?>? onActionStarting = null)
    {
        _transport = transport;
        _logger = logger;
        _goal = goal;
        _completion = completion;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _tabId = tabId;
        _expectedFirstUrl = expectedFirstUrl;
        _onActionStarting = onActionStarting;
    }

    public string SessionId => _sessionId;
    public int? TabId => _tabId;
    public BrowserSnapshot? LatestSnapshot { get; private set; }
    /// <summary>Revision of the observation whose snapshot can still be acted on.</summary>
    public long CurrentRevision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Hands the surface a snapshot obtained by a startup/selection call that ran concurrently
    /// with normalization. Always adopts tab identity so the engine's first <see cref="ObserveAsync"/>
    /// no longer treats this as a cold startup. When <paramref name="reuseAsFirstObservation"/> is
    /// false the page may have hydrated further while normalization ran, so the engine's first
    /// observation still fetches a fresh snapshot instead of reusing this one.
    /// </summary>
    internal void Prepare(BrowserSnapshot snapshot, bool reuseAsFirstObservation)
    {
        ValidateOwnership(snapshot);
        _tabId = snapshot.TabId;
        LatestSnapshot = snapshot;
        _pendingAcquisition = ("opened", null);
        if (reuseAsFirstObservation)
            _preparedSnapshot = snapshot;
    }

    public async ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        _actionSnapshot = null;
        if (_preparedSnapshot is { } prepared)
        {
            _preparedSnapshot = null;
            _logger?.LogInformation("Browser stage=observation source=prepared_startup reused=true");
            return Accept(prepared);
        }
        var startup = _tabId is null;
        var transportTimer = Stopwatch.StartNew();
        if (startup) _onActionStarting?.Invoke(null);
        var snapshot = _tabId is int tabId
            ? await _transport.ObserveAsync(_sessionId, tabId, cancellationToken).ConfigureAwait(false)
            : await _transport.OpenTaskTabAsync(_sessionId, BrowserGoal.BootstrapUrl(_goal), cancellationToken).ConfigureAwait(false);
        transportTimer.Stop();
        if (startup) _pendingAcquisition = ("opened", null);
        LatencyTrace.Current?.Record(startup ? "tab_startup_and_first_observation"
            : _revision == 0 ? "first_observation" : "observation", transportTimer.Elapsed.TotalMilliseconds);
        _logger?.LogInformation("Browser stage={Stage} transport_ms={ElapsedMs:F0}",
            startup ? "tab_startup_and_first_observation" : "observation", transportTimer.Elapsed.TotalMilliseconds);
        ValidateOwnership(snapshot);
        if (_expectedFirstUrl is { } expected)
        {
            _expectedFirstUrl = null;
            if (!StringComparer.Ordinal.Equals(snapshot.Url, expected))
                throw new ChromeCompanionException("STALE_TAB",
                    "The selected tab navigated before its first observation.");
        }
        return Accept(snapshot);
    }

    public ValueTask<InteractionObservation> ObserveAfterActionAsync(CancellationToken cancellationToken = default)
    {
        if (_actionSnapshot is { } snapshot)
        {
            _actionSnapshot = null;
            _logger?.LogInformation("Browser stage=observation source=action_result");
            return ValueTask.FromResult(Accept(snapshot));
        }
        return ObserveAsync(cancellationToken);
    }

    private InteractionObservation Accept(BrowserSnapshot snapshot)
    {
        _tabId = snapshot.TabId;
        LatestSnapshot = snapshot;
        _logger?.LogInformation("Browser observation origin={Origin} revision={Revision}",
            LogOrigin(snapshot.Url), snapshot.Revision);

        var revision = Interlocked.Increment(ref _revision);
        _snapshots.Clear();
        _snapshots[revision] = snapshot;
        var candidates = BuildCandidates(snapshot, revision);
        var (evidence, stateKey) = _actionEvidence is { } cached && ReferenceEquals(cached.Snapshot, snapshot)
            ? (cached.Evidence, cached.StateKey) : BuildEvidence(snapshot);
        _actionEvidence = null;
        IReadOnlyList<Effect>? effects = null;
        if (_pendingAcquisition is { } acquired)
        {
            _pendingAcquisition = null;
            effects = [BrowserEffectEmitter.SurfaceAcquired(snapshot, revision, acquired.Mode, acquired.FromTabId)];
        }
        return new(revision, stateKey, evidence, candidates, effects);
    }

    private (string Evidence, string StateKey) BuildEvidence(BrowserSnapshot snapshot)
    {
        var facts = BrowserDomFacts.Derive(snapshot.Elements);
        var evidence = JsonSerializer.Serialize(new
        {
            original_goal = _goal.OriginalUtterance,
            hints = new { _goal.NamedServiceHint },
            current_url = snapshot.Url,
            current_origin = LogOrigin(snapshot.Url),
            current_title = snapshot.Title,
            visible_text = snapshot.VisibleText,
            viewport = snapshot.Viewport,
            elements = snapshot.Elements.Select(element => ElementEvidence(element, facts[element.Ref]))
        });
        return (evidence, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))));
    }

    /// <summary>The always-present element fields plus only those derived facts that say something.</summary>
    private static Dictionary<string, object?> ElementEvidence(BrowserElement element, ElementFacts facts)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = element.Ref, ["Role"] = element.Role, ["Name"] = element.Name, ["Value"] = element.Value,
            ["Href"] = element.Href, ["Context"] = element.Context, ["Editable"] = element.Editable,
            ["Enabled"] = element.Enabled, ["InViewport"] = element.Geometry.InViewport
        };
        if (facts.Kind is BrowserDomFacts.SearchField or BrowserDomFacts.SearchOpener or BrowserDomFacts.SubmitControl
            or BrowserDomFacts.NavigationLink or BrowserDomFacts.ResultItem) entry["Kind"] = facts.Kind;
        if (facts.SearchScope is not null) entry["SearchScope"] = facts.SearchScope;
        if (facts.SubmitRef is not null) entry["SubmitRef"] = facts.SubmitRef;
        if (element.Form is not null) entry["Form"] = element.Form;
        if (element.Submit) entry["Submit"] = true;
        if (facts.Landmark is not null) entry["Landmark"] = facts.Landmark;
        if (facts.Selected is { } selected) entry["Selected"] = selected;
        return entry;
    }

    public async ValueTask<InteractionActionResult> ExecuteAsync(
        InteractionAction action, InteractionObservation observation,
        CancellationToken cancellationToken = default)
    {
        _actionSnapshot = null;
        _actionEvidence = null;
        if (!_snapshots.TryGetValue(observation.Revision, out var snapshot))
            return InteractionActionResult.Fail(InteractionResultStatus.StaleTarget, "The observation revision is stale.");
        if (!observation.Candidates.SelectMany(static candidate => candidate.Actions).Any(offered =>
                offered.Id == action.Id && offered.Kind == action.Kind
                && offered.TargetId == action.TargetId && offered.Direction == action.Direction))
            return InteractionActionResult.Fail(InteractionResultStatus.ScopeViolation,
                "The action is not compatible with the current observation.");
        if (action.Kind is InteractionActionKind.SetText or InteractionActionKind.TypeText
            && (string.IsNullOrWhiteSpace(action.Text) || action.Text.Length > 240
                || action.Text.Any(char.IsControl)))
            return InteractionActionResult.Fail(InteractionResultStatus.ScopeViolation, "The selected field value is missing or outside the bounded text format.");

        var protocolAction = action.Kind switch
        {
            InteractionActionKind.Activate => "CLICK",
            InteractionActionKind.SetText => "REPLACE_TEXT",
            InteractionActionKind.TypeText => "INSERT_TEXT",
            InteractionActionKind.Scroll => "SCROLL",
            InteractionActionKind.GoBack => "BACK",
            InteractionActionKind.PressKey => "SUBMIT",
            _ => null
        };
        if (protocolAction is null)
            return InteractionActionResult.Fail(InteractionResultStatus.UnsupportedAction, "The companion does not support this action.");

        try
        {
            _onActionStarting?.Invoke(action);
            var actionTimer = Stopwatch.StartNew();
            var next = await _transport.ActAsync(new(
                snapshot.TabId, _sessionId, snapshot.Revision, protocolAction,
                action.TargetId, action.Text, action.Direction), cancellationToken).ConfigureAwait(false);
            actionTimer.Stop();
            LatencyTrace.Current?.Record($"action_transport_{protocolAction.ToLowerInvariant()}",
                actionTimer.Elapsed.TotalMilliseconds);
            _logger?.LogInformation("Browser stage=action_transport operation={Operation} elapsed_ms={ElapsedMs:F0}",
                protocolAction, actionTimer.Elapsed.TotalMilliseconds);
            var adopted = next.TabId != snapshot.TabId;
            if (adopted)
            {
                if (action.Kind != InteractionActionKind.Activate
                    || next.AdoptedFromTabId != snapshot.TabId
                    || !StringComparer.Ordinal.Equals(next.SessionId, _sessionId))
                    throw new ChromeCompanionException("TAB_TOPOLOGY_AMBIGUOUS",
                        "The action changed tabs without a verified task-surface transition.");
                _tabId = next.TabId;
                _pendingAcquisition = ("adopted", snapshot.TabId);
                _logger?.LogInformation("Browser adopted task tab old={OldTab} new={NewTab}",
                    snapshot.TabId, next.TabId);
            }
            else ValidateOwnership(next);
            LatestSnapshot = next;
            _actionSnapshot = next;
            _logger?.LogInformation("Browser action operation={Operation} ref={Ref} outcome=success origin={Origin}",
                protocolAction, action.TargetId, LogOrigin(next.Url));
            var (nextEvidence, nextKey) = BuildEvidence(next);
            _actionEvidence = (next, nextEvidence, nextKey);
            var effects = BrowserEffectEmitter.ForSuccess(action, protocolAction, observation.Revision,
                snapshot, next, !StringComparer.Ordinal.Equals(nextKey, observation.StateKey), adopted);
            return InteractionActionResult.Ok("The companion executed one bounded action.", effects);
        }
        catch (ChromeCompanionException ex)
        {
            if (ex.Code == "TRANSPORT_DISCONNECTED")
                throw new InfrastructureUnavailableException(UnavailableReason.ChromeCompanion,
                    "Chrome companion isn't connected.", ex);
            _logger?.LogWarning("Browser action operation={Operation} ref={Ref} outcome={Code}", protocolAction, action.TargetId, ex.Code);
            var status = ex.Code switch
            {
                "STALE_REVISION" or "STALE_ELEMENT" => InteractionResultStatus.StaleTarget,
                "TAB_NOT_OWNED" or "SESSION_MISMATCH" => InteractionResultStatus.ScopeViolation,
                "TAB_TOPOLOGY_AMBIGUOUS"
                    => InteractionResultStatus.TopologyAmbiguous,
                "ELEMENT_NOT_VISIBLE" or "ELEMENT_DISABLED" => InteractionResultStatus.TargetUnavailable,
                // The companion confirmed no navigation happened: the tab has no earlier entry,
                // or the traversal left the page unchanged. Unconfirmed or failed navigation
                // (NAVIGATION_UNCONFIRMED / NAVIGATION_FAILED) remains a platform failure.
                "NO_HISTORY" or "NAVIGATION_NOT_OBSERVED" => InteractionResultStatus.NoEffect,
                _ => InteractionResultStatus.PlatformFailure
            };
            return InteractionActionResult.Fail(status, ex.Message,
                status == InteractionResultStatus.NoEffect ? BrowserEffectEmitter.ForNoEffectFailure(ex.Code) : null);
        }
    }

    public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(
        InteractionGoal goal, InteractionObservation observation,
        IReadOnlyList<InteractionHistoryEntry> recentHistory,
        CancellationToken cancellationToken = default)
        => _completion.AssessAsync(_goal, observation, recentHistory, cancellationToken);

    private void ValidateOwnership(BrowserSnapshot snapshot)
    {
        if (!StringComparer.Ordinal.Equals(snapshot.SessionId, _sessionId))
            throw new ChromeCompanionException("SESSION_MISMATCH", "The companion returned a different browser session.");
        if (_tabId is int tabId && snapshot.TabId != tabId)
            throw new ChromeCompanionException("TAB_NOT_OWNED", "The companion changed task tabs.");
    }

    internal static string LogOrigin(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) : "unknown";

    private static IReadOnlyList<InteractionCandidate> BuildCandidates(BrowserSnapshot snapshot, long revision)
    {
        var result = new List<InteractionCandidate>();
        var facts = BrowserDomFacts.Derive(snapshot.Elements);
        foreach (var element in snapshot.Elements.Where(static item => item.Enabled))
        {
            var fact = facts[element.Ref];
            var label = $"{element.Role} '{element.Name}' value='{element.Value}' context='{element.Context}'"
                + (element.Search ? " purpose='search'" : "")
                + (fact.Kind == BrowserDomFacts.SearchOpener ? " opens='search'" : "")
                + (fact.SearchScope is { } scope ? $" scope='{scope}'" : "")
                + (fact.Selected is { } selected ? $" selected={selected.ToString().ToLowerInvariant()}" : "")
                + (fact.Landmark is "navigation" ? " region='navigation'" : "")
                + (fact.InList && element.Role == "link" ? " result_item=true" : "");
            var actions = new List<InteractionAction>();
            if (element.Editable)
            {
                actions.Add(new($"r{revision}:replace:{element.Ref}", InteractionActionKind.SetText, element.Ref));
                if (!string.IsNullOrEmpty(element.Value))
                {
                    actions.Add(new($"r{revision}:insert:{element.Ref}", InteractionActionKind.TypeText, element.Ref));
                    // Enter applies a field that holds text (search boxes without a submit button).
                    if (element.Search || element.Role == "searchbox" || facts[element.Ref].Kind == BrowserDomFacts.SearchField)
                        actions.Add(new($"r{revision}:submit:{element.Ref}", InteractionActionKind.PressKey, element.Ref));
                }
            }
            if (element.Role is "button" or "link" or "tab" or "menuitem" or "option" or "checkbox" or "radio" or "summary")
                actions.Add(new($"r{revision}:click:{element.Ref}", InteractionActionKind.Activate, element.Ref));
            if (actions.Count > 0)
                result.Add(new(element.Ref, label, actions));
        }
        result.Add(new("page", "Current page", [
            new($"r{revision}:scroll:down", InteractionActionKind.Scroll, Direction: "down"),
            new($"r{revision}:scroll:up", InteractionActionKind.Scroll, Direction: "up")
        ]));
        if (snapshot.CanGoBack)
            result.Add(new("history", "Task-tab history", [new($"r{revision}:back", InteractionActionKind.GoBack)]));
        return result;
    }
}
