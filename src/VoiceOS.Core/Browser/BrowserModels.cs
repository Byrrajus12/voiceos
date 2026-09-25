using System.Text.Json.Serialization;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Browser;

public enum CommandRoute { DirectCapability, ComputerUse, NativeInteraction, TextTransform, Clarify }
public enum RoutingReason { None, MediaTransport, NewContentTarget, IncompleteIntent, LowConfidence, AmbiguousIntent, RouterFailure }
public enum MediaRequestKind { None, Transport, ContentSelection, Uncertain }
public enum SemanticDestinationKind { None, KnownService, ExplicitUrl, NamedTab }
public enum TabDisposition { Unspecified, CurrentTab, NewTab, ExistingNamedTab }
public enum SurfacePreference { Unspecified, Native, Browser }
public enum SemanticEndState { Unspecified, SurfaceReady, ResultsVisible, ResourceLocated, ResourceOpened, ContentActive, StateChanged, OtherBoundedGoal }
public enum GoalShape { Uncertain, SurfaceOnly, ActionOnSurface }
public enum TaskRelation { NewTask, ContinueRecent, RequiresRecent, Uncertain }
public enum ContextDependency { Uncertain, SelfContained, RequiresCurrentSurface }
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
    ContextDependency ContextDependency = ContextDependency.Uncertain);
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
    [property: JsonPropertyName("context")] string Context);

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
    [property: JsonPropertyName("adoptedFromTabId")] int? AdoptedFromTabId = null);

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

public enum ContextualSurface { ActiveBrowserTab, RecentOwnedBrowserTab, NewBrowserTaskTab, ForegroundNativeWindow, Clarify }
public interface IContextualScopeDecisionSource
{
    ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
        ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered,
        CancellationToken cancellationToken = default);
    ValueTask<int?> SelectNamedTabAsync(string utterance, IReadOnlyList<BrowserTabInfo> tabs,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<int?>(null);
    ValueTask<string?> SelectInstalledAppAsync(string utterance,
        IReadOnlyList<VoiceOS.Core.Candidates.AppCandidate> apps, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);
}

public sealed record BrowserInteractionOutcome(
    InteractionCompletionState Completion, string? Detail,
    IReadOnlyList<InteractionChoice>? Choices, string? Url, string? Title,
    int Decisions = 0, int Actions = 0, int? TabId = null, string? SessionId = null,
    string? SemanticGoal = null);
