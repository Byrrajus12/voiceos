using System.Text.Json.Serialization;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Browser;

public enum CommandRoute { DirectCapability, ComputerUse, NativeInteraction, TextTransform, Clarify }
public enum RoutingReason { None, MediaTransport, NewContentTarget, IncompleteIntent, LowConfidence, AmbiguousIntent, RouterFailure,
    /// <summary>Going back was requested but neither media playback nor navigation was established.</summary>
    UnresolvedReturn, DirectRescue }
public enum MediaRequestKind { None, Transport, ContentSelection, Uncertain }
public enum SemanticDestinationKind { None, KnownService, ExplicitUrl, NamedTab }
public enum TabDisposition { Unspecified, CurrentTab, NewTab, ExistingNamedTab }
public enum SurfacePreference { Unspecified, Native, Browser }
public enum SemanticEndState { Unspecified, SurfaceReady, ResultsVisible, ResourceLocated, ResourceOpened, ContentActive, StateChanged, OtherBoundedGoal }
public enum GoalShape { Uncertain, SurfaceOnly, ActionOnSurface }
public enum TaskRelation { NewTask, ContinueRecent, RequiresRecent, Uncertain }
public enum ContextDependency { Uncertain, SelfContained, RequiresCurrentSurface }
/// <summary>What a back/previous request returns through; separates media Previous from navigation Back.</summary>
public enum ReturnTarget { Uncertain, None, MediaPlayback, NavigationHistory }
/// <summary>A request that is exactly one known page operation, framed by the router so no further model call is needed.</summary>
public enum PageOperation { None, Back, Forward, ScrollDown, ScrollUp }
/// <summary>The requested thing to open or use, independent of the surface that could host it.</summary>
public enum RequestedEntityKind { Uncertain, None, BrowserItself, NamedEntity }
public sealed record CommandRouteDecision(CommandRoute Route, double Confidence, string? Detail = null,
    RoutingReason Reason = RoutingReason.None, MediaOperation? MediaOperation = null,
    MediaRequestKind MediaRequestKind = MediaRequestKind.Uncertain,
    SemanticDestinationKind DestinationKind = SemanticDestinationKind.None,
    string? DestinationName = null, Uri? ExplicitUrl = null,
    TabDisposition TabDisposition = TabDisposition.Unspecified,
    SurfacePreference SurfacePreference = SurfacePreference.Unspecified,
    SemanticEndState EndState = SemanticEndState.Unspecified,
    TaskRelation TaskRelation = TaskRelation.NewTask,
    string? NamedTabTarget = null,
    GoalShape GoalShape = GoalShape.Uncertain,
    ContextDependency ContextDependency = ContextDependency.Uncertain,
    RequestedEntityKind RequestedEntity = RequestedEntityKind.Uncertain)
{
    public IReadOnlyDictionary<string, JevAnswer>? RawAnswers { get; init; }
    public bool TaskRelationEstablished { get; init; }
    /// <summary>The router judged that the request denotes something opened or used earlier (asked only when earlier referents exist).</summary>
    public bool ReferencesEarlier { get; init; }
    public bool IntentActionable { get; init; }
    /// <summary>The coarse head proposed browser execution before its confidence gate.
    /// This is corroboration only, never sufficient to select a surface.</summary>
    public bool CoarseBrowserCandidate { get; init; }
    public bool CoarseNonBrowserCandidate { get; init; }
    public ReturnTarget ReturnTarget { get; init; } = ReturnTarget.Uncertain;
    /// <summary>The whole request is one history traversal or scroll of the current page.</summary>
    public PageOperation PageOperation { get; init; }
    /// <summary>A service the classifier proposed but the user did not name; never authoritative.</summary>
    public string? SuggestedDestination { get; init; }
    /// <summary>A specific non-browser app, service, or site was requested. A generic browser
    /// host (e.g. Chrome itself) cannot satisfy it.</summary>
    public bool RequestsNamedEntity => RequestedEntity == RequestedEntityKind.NamedEntity
        || DestinationKind == SemanticDestinationKind.KnownService;
}
public interface ICommandRouter
{
    ValueTask<CommandRouteDecision> RouteAsync(string transcript, CancellationToken cancellationToken = default);
}

public sealed record BrowserGeometry(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("inViewport")] bool InViewport);

public sealed record BrowserElement(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("editable")] bool Editable,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("geometry")] BrowserGeometry Geometry,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("search")] bool Search = false,
    [property: JsonPropertyName("form")] string? Form = null,
    [property: JsonPropertyName("submit")] bool Submit = false,
    [property: JsonPropertyName("formAction")] string? FormAction = null,
    [property: JsonPropertyName("landmark")] string? Landmark = null,
    [property: JsonPropertyName("inList")] bool InList = false,
    [property: JsonPropertyName("selected")] bool? Selected = null,
    [property: JsonPropertyName("collection")] string? Collection = null,
    [property: JsonPropertyName("position")] int? Position = null,
    [property: JsonPropertyName("collectionSize")] int? CollectionSize = null);

public sealed record BrowserViewport(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("scrollX")] int ScrollX,
    [property: JsonPropertyName("scrollY")] int ScrollY,
    [property: JsonPropertyName("documentHeight")] int DocumentHeight = 0);

public sealed record BrowserSnapshot(
    [property: JsonPropertyName("tabId")] int TabId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("visibleText")] string VisibleText,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("viewport")] BrowserViewport Viewport,
    [property: JsonPropertyName("elements")] IReadOnlyList<BrowserElement> Elements,
    [property: JsonPropertyName("canGoBack")] bool CanGoBack = false,
    [property: JsonPropertyName("adoptedFromTabId")] int? AdoptedFromTabId = null,
    [property: JsonIgnore] BrowserActionSignals? Signals = null,
    // Task-level Back with no same-tab history: the companion returned to the tab this one was opened from.
    [property: JsonPropertyName("returnedFromTabId")] int? ReturnedFromTabId = null,
    // Grounded tab facts from the companion (Chrome owns the history itself; these are ownership/lineage).
    [property: JsonPropertyName("ownedByVoiceOS")] bool? OwnedByVoiceOs = null,
    [property: JsonPropertyName("adopted")] bool? Adopted = null,
    [property: JsonPropertyName("openerTabId")] int? OpenerTabId = null,
    [property: JsonPropertyName("documentId")] string? DocumentId = null,
    // Longer body text than VisibleText, used only to read a value a later plan step needs.
    [property: JsonPropertyName("pageText")] string? PageText = null);

/// <summary>
/// Navigation signal the companion reported alongside an ACT snapshot (<c>timings.signal</c>:
/// none | cross_document | same_document | new_tab). "none" is a meaningful negative only for
/// CLICK and BACK; text and scroll actions have no navigation watcher and always report it.
/// </summary>
public sealed record BrowserActionSignals(string Navigation, string? SettleReason = null)
{
    public static bool IsMeaningfulFor(string protocolAction) => protocolAction is "CLICK" or "BACK" or "FORWARD" or "SUBMIT";
}

public sealed record BrowserActionRequest(
    int TabId, string SessionId, string Revision, string Action,
    string? ElementRef = null, string? Text = null, string? Direction = null);

public sealed class ChromeCompanionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IChromeCompanionTransport
{
    ValueTask<BrowserTabInfo> CreateNewTabAsync(string sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Normal new-tab creation is unavailable.");
    ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default);
    ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default);
    ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default);
    ValueTask FocusTaskTabAsync(string sessionId, int tabId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    /// <summary>Best-effort cleanup of a task tab this session started but never used (e.g. normalization
    /// failed while startup was overlapped). The extension refuses to close adopted user tabs or other
    /// sessions' tabs, so failures here are swallowed by the caller.</summary>
    ValueTask CloseTaskTabAsync(string sessionId, int tabId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    bool IsConnected => false;
    ValueTask<IReadOnlyList<BrowserTabInfo>> ListTabsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<BrowserTabInfo>>([]);
    ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl,
        bool requireActive, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Tab selection is unavailable.");
    ValueTask SelectTabWithTitleAsync(string sessionId, int tabId, string expectedUrl,
        string expectedTitle, bool requireActive, CancellationToken cancellationToken = default)
        => SelectTabAsync(sessionId, tabId, expectedUrl, requireActive, cancellationToken);
}

public enum ContextualSurface { ActiveBrowserTab, RecentOwnedBrowserTab, NewBrowserTaskTab, ForegroundNativeWindow, Clarify, DirectCapability }

/// <summary>How the real tab inventory settled a named-tab claim.</summary>
public enum NamedTabSelectionKind
{
    /// <summary>Exactly one open tab is the named tab.</summary>
    Selected,
    /// <summary>No open tab matches: the named-tab claim is refuted.</summary>
    NoMatch,
    /// <summary>Several open tabs plausibly match.</summary>
    Ambiguous,
    /// <summary>The inventory could not be judged (no picker, invalid or low-confidence answer).</summary>
    Unavailable
}

public sealed record NamedTabSelection(NamedTabSelectionKind Kind, int? TabId = null, string? Reason = null)
{
    public static NamedTabSelection Select(int tabId) => new(NamedTabSelectionKind.Selected, tabId);
    public static NamedTabSelection NoMatch(string reason) => new(NamedTabSelectionKind.NoMatch, Reason: reason);
    public static NamedTabSelection Ambiguous(string reason) => new(NamedTabSelectionKind.Ambiguous, Reason: reason);
    public static NamedTabSelection Unavailable(string reason) => new(NamedTabSelectionKind.Unavailable, Reason: reason);
}
public interface IContextualScopeDecisionSource
{
    ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
        ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered,
        CancellationToken cancellationToken = default);
    ValueTask<NamedTabSelection> SelectNamedTabAsync(string utterance, IReadOnlyList<BrowserTabInfo> tabs,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(NamedTabSelection.Unavailable("no_named_tab_picker"));
    ValueTask<string?> SelectInstalledAppAsync(string utterance,
        IReadOnlyList<VoiceOS.Core.Candidates.AppCandidate> apps, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);
    /// <summary>Focused choice over typed, validated referents; one request with two heads, no generation.</summary>
    ValueTask<ReferentChoice> SelectReferentAsync(string utterance, IReadOnlyList<ReferentCandidate> candidates,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ReferentChoice(ReferentChoiceKind.Unavailable));
}

public sealed record BrowserInteractionOutcome(
    InteractionCompletionState Completion, string? Detail,
    IReadOnlyList<InteractionChoice>? Choices, string? Url, string? Title,
    int Decisions = 0, int Actions = 0, int? TabId = null, string? SessionId = null,
    string? SemanticGoal = null, UnavailableReason? Unavailable = null,
    IReadOnlyList<Referent>? Referents = null);
