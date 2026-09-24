using System.Text.Json.Serialization;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Browser;

public enum CommandRoute { DirectCapability, ComputerUse, NativeInteraction, TextTransform, Clarify }
public enum RoutingReason { None, MediaTransport, NewContentTarget, IncompleteIntent, LowConfidence, AmbiguousIntent, RouterFailure }
public enum MediaRequestKind { None, Transport, ContentSelection, Uncertain }
public sealed record CommandRouteDecision(CommandRoute Route, double Confidence, string? Detail = null,
    RoutingReason Reason = RoutingReason.None, MediaOperation? MediaOperation = null,
    MediaRequestKind MediaRequestKind = MediaRequestKind.Uncertain);
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
    [property: JsonPropertyName("canGoBack")] bool CanGoBack = false);

public sealed record BrowserActionRequest(
    int TabId, string SessionId, string Revision, string Action,
    string? ElementRef = null, string? Text = null, string? Direction = null);

public sealed class ChromeCompanionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IChromeCompanionTransport
{
    ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default);
    ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default);
    ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default);
    ValueTask FocusTaskTabAsync(string sessionId, int tabId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public sealed record BrowserInteractionOutcome(
    InteractionCompletionState Completion, string? Detail,
    IReadOnlyList<InteractionChoice>? Choices, string? Url, string? Title,
    int Decisions = 0, int Actions = 0);
