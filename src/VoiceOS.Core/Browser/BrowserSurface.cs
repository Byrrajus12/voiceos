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
        var evidence = JsonSerializer.Serialize(new
        {
            original_goal = _goal.OriginalUtterance,
            hints = new { _goal.NamedServiceHint },
            current_url = snapshot.Url,
            current_title = snapshot.Title,
            visible_text = snapshot.VisibleText,
            viewport = snapshot.Viewport,
            elements = snapshot.Elements.Select(static element => new
            {
                id = element.Ref, element.Role, element.Name, element.Value, element.Href,
                element.Context, element.Editable, element.Enabled, element.Geometry.InViewport
            })
        });
        var stateKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        return new(revision, stateKey, evidence, candidates);
    }

    public async ValueTask<InteractionActionResult> ExecuteAsync(
        InteractionAction action, InteractionObservation observation,
        CancellationToken cancellationToken = default)
    {
        _actionSnapshot = null;
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
            if (next.TabId != snapshot.TabId)
            {
                if (action.Kind != InteractionActionKind.Activate
                    || next.AdoptedFromTabId != snapshot.TabId
                    || !StringComparer.Ordinal.Equals(next.SessionId, _sessionId))
                    throw new ChromeCompanionException("TAB_TOPOLOGY_AMBIGUOUS",
                        "The action changed tabs without a verified task-surface transition.");
                _tabId = next.TabId;
                _logger?.LogInformation("Browser adopted task tab old={OldTab} new={NewTab}",
                    snapshot.TabId, next.TabId);
            }
            else ValidateOwnership(next);
            LatestSnapshot = next;
            _actionSnapshot = next;
            _logger?.LogInformation("Browser action operation={Operation} ref={Ref} outcome=success origin={Origin}",
                protocolAction, action.TargetId, LogOrigin(next.Url));
            return InteractionActionResult.Ok("The companion executed one bounded action.");
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
            return InteractionActionResult.Fail(status, ex.Message);
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
        foreach (var element in snapshot.Elements.Where(static item => item.Enabled))
        {
            var label = $"{element.Role} '{element.Name}' value='{element.Value}' context='{element.Context}'"
                + (element.Search ? " purpose='search'" : "");
            var actions = new List<InteractionAction>();
            if (element.Editable)
            {
                actions.Add(new($"r{revision}:replace:{element.Ref}", InteractionActionKind.SetText, element.Ref));
                if (!string.IsNullOrEmpty(element.Value))
                    actions.Add(new($"r{revision}:insert:{element.Ref}", InteractionActionKind.TypeText, element.Ref));
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
