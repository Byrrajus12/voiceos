using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Long-lived VoiceOS endpoint for the MV3/native-host bridge. Connections may be
/// replaced, but a command is never replayed after it has been written.
/// </summary>
public sealed class ChromeCompanionTransport : IChromeCompanionTransport, IDisposable
{
    public const string PipeName = "VoiceOS.ChromeCompanion.Alpha";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<ChromeCompanionTransport> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _connectionLock = new();
    private TaskCompletionSource<Connection> _nextConnection = NewConnectionSource();
    private Connection? _connection;
    private Task? _listener;
    private int _nextCommandId;

    public ChromeCompanionTransport(ILogger<ChromeCompanionTransport> logger) => _logger = logger;

    public void Start()
    {
        if (_listener is not null) return;
        lock (_connectionLock)
        {
            if (_listener is not null) return;
            _listener = Task.Run(() => ListenAsync(_shutdown.Token));
            _logger.LogInformation("Chrome Companion transport is listening; the extension connects automatically.");
        }
    }

    public ValueTask<BrowserSnapshot> OpenTaskTabAsync(
        string sessionId, string url, CancellationToken cancellationToken = default)
        => SendAsync("OPEN_TASK_TAB", new { sessionId, url }, cancellationToken);

    public ValueTask<BrowserSnapshot> ObserveAsync(
        string sessionId, int tabId, CancellationToken cancellationToken = default)
        => SendAsync("OBSERVE", new { sessionId, tabId }, cancellationToken);

    public ValueTask<BrowserSnapshot> ActAsync(
        BrowserActionRequest action, CancellationToken cancellationToken = default)
        => SendAsync("ACT", new
        {
            sessionId = action.SessionId, tabId = action.TabId, revision = action.Revision,
            action = action.Action, elementRef = action.ElementRef,
            text = action.Text, direction = action.Direction
        }, cancellationToken);

    public async ValueTask FocusTaskTabAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
        => _ = await SendAsync("FOCUS_TASK_TAB", new { sessionId, tabId }, cancellationToken).ConfigureAwait(false);

    private async ValueTask<BrowserSnapshot> SendAsync<T>(
        string command, T payload, CancellationToken cancellationToken)
    {
        Start();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            var id = $"voiceos-{Interlocked.Increment(ref _nextCommandId)}";
            var written = false;
            try
            {
                await LengthPrefixedJson.WriteAsync(connection.Stream,
                    new { type = "command", id, command, payload }, cancellationToken).ConfigureAwait(false);
                written = true;
                while (true)
                {
                    using var response = await LengthPrefixedJson.ReadAsync(connection.Stream, cancellationToken).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("The Chrome companion disconnected before responding.");
                    var root = response.RootElement;
                    if (OptionalString(root, "type") != "response" || OptionalString(root, "id") != id)
                        continue;
                    if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    {
                        var error = root.TryGetProperty("error", out var value) ? value : default;
                        throw new ChromeCompanionException(
                            OptionalString(error, "code") ?? "EXTENSION_ERROR",
                            OptionalString(error, "message") ?? $"Chrome companion command {command} failed.");
                    }
                    if (!root.TryGetProperty("result", out var result)
                        || !result.TryGetProperty("snapshot", out var snapshot))
                        throw new InvalidDataException("Chrome companion response omitted result.snapshot.");
                    return snapshot.Deserialize<BrowserSnapshot>(JsonOptions)
                        ?? throw new InvalidDataException("Chrome companion returned an invalid snapshot.");
                }
            }
            catch (ChromeCompanionException) { throw; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                Invalidate(connection);
                var detail = written
                    ? "The companion disconnected after the command was sent; VoiceOS will not replay an uncertain action."
                    : "The companion disconnected before the command was sent; retry the task after it reconnects.";
                throw new ChromeCompanionException("TRANSPORT_DISCONNECTED", detail);
            }
        }
        finally { _commandGate.Release(); }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var bridge = await RequireMessageAsync(pipe, "bridge_hello", cancellationToken).ConfigureAwait(false);
                using var extension = await RequireMessageAsync(pipe, "extension_hello", cancellationToken).ConfigureAwait(false);
                var origin = OptionalString(bridge.RootElement, "origin");
                var extensionId = OptionalString(extension.RootElement, "extensionId");
                if (extensionId is null || !StringComparer.OrdinalIgnoreCase.Equals(origin, $"chrome-extension://{extensionId}/"))
                    throw new InvalidDataException("Native host origin did not match the extension id.");

                var connection = new Connection(pipe);
                lock (_connectionLock)
                {
                    _connection?.Dispose();
                    _connection = connection;
                    _nextConnection.TrySetResult(connection);
                }
                _logger.LogInformation("Chrome Companion connected. ExtensionId={ExtensionId}", extensionId);
                pipe = null;
                await connection.Closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Chrome Companion connection ended."); }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        lock (_connectionLock)
            if (_connection is { } current)
                return current;
        return await _nextConnection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Invalidate(Connection connection)
    {
        lock (_connectionLock)
        {
            if (!ReferenceEquals(_connection, connection))
                return;
            _connection = null;
            _nextConnection = NewConnectionSource();
            connection.Dispose();
        }
    }

    private static TaskCompletionSource<Connection> NewConnectionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<JsonDocument> RequireMessageAsync(
        Stream stream, string expectedType, CancellationToken cancellationToken)
    {
        var message = await LengthPrefixedJson.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException($"Disconnected while awaiting {expectedType}.");
        if (OptionalString(message.RootElement, "type") == expectedType)
            return message;
        message.Dispose();
        throw new InvalidDataException($"Expected {expectedType}.");
    }

    private static string? OptionalString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_connectionLock) { _connection?.Dispose(); _connection = null; }
        _commandGate.Dispose();
        _shutdown.Dispose();
    }

    private sealed class Connection(Stream stream) : IDisposable
    {
        public Stream Stream { get; } = stream;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            Stream.Dispose();
            Closed.TrySetResult();
        }
    }
}

internal static class LengthPrefixedJson
{
    private const int MaximumMessageBytes = 1_048_576;

    public static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var prefixBytes = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixBytes == 0) return null;
        if (prefixBytes != prefix.Length) throw new EndOfStreamException("Incomplete message prefix.");
        var length = BitConverter.ToInt32(prefix);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException($"Invalid message length {length}.");
        var payload = new byte[length];
        if (await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false) != length)
            throw new EndOfStreamException("Incomplete message payload.");
        return JsonDocument.Parse(payload);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException($"Invalid message length {payload.Length}.");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
