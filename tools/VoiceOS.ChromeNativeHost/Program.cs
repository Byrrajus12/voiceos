using System.IO.Pipes;
using System.Text.Json;

namespace VoiceOS.ChromeNativeHost;

internal static class Program
{
    private const string PipeName = "VoiceOS.ChromeCompanion.Alpha";
    private const int ConnectTimeoutMs = 5_000;

    private static async Task<int> Main(string[] args)
    {
        // stdout is exclusively the Chrome Native Messaging binary channel.
        Console.Error.WriteLine("VoiceOS Chrome Native Host starting.");

        try
        {
            await using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
            await pipe.ConnectAsync(timeout.Token);

            var origin = args.FirstOrDefault(static value =>
                value.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
            await LengthPrefixedJson.WriteAsync(pipe, new
            {
                type = "bridge_hello",
                protocolVersion = 1,
                origin = origin ?? "unknown"
            }, CancellationToken.None);

            using var stopped = new CancellationTokenSource();
            var chromeInput = Console.OpenStandardInput();
            var chromeOutput = Console.OpenStandardOutput();

            var fromChrome = PumpFramesAsync(chromeInput, pipe, stopped.Token);
            var fromVoiceOS = PumpFramesAsync(pipe, chromeOutput, stopped.Token);
            await Task.WhenAny(fromChrome, fromVoiceOS);
            stopped.Cancel();

            // Console stdin cancellation is not reliable on every Windows runtime.
            // Do not let that keep the native host alive after VoiceOS closes its pipe.
            var bothPumps = Task.WhenAll(fromChrome, fromVoiceOS);
            var completed = await Task.WhenAny(bothPumps, Task.Delay(250));
            if (completed == bothPumps)
                await bothPumps;

            Console.Error.WriteLine("VoiceOS Chrome Native Host disconnected; Chrome remains running.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("VoiceOS is not listening. Start VoiceOS before clicking the extension action.");
            return 2;
        }
        catch (EndOfStreamException)
        {
            Console.Error.WriteLine("Native Messaging peer disconnected.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Native host failure: {ex.Message}");
            return 1;
        }
    }

    private static async Task PumpFramesAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = await LengthPrefixedJson.ReadAsync(input, cancellationToken);
            if (message is null)
                return;
            await LengthPrefixedJson.WriteAsync(output, message.RootElement, cancellationToken);
        }
    }
}

internal static class LengthPrefixedJson
{
    private const int MaximumMessageBytes = 1_048_576;

    public static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var prefixBytes = await ReadAtMostAsync(stream, prefix, cancellationToken);
        if (prefixBytes == 0)
            return null;
        if (prefixBytes != prefix.Length)
            throw new EndOfStreamException("Message length prefix was incomplete.");

        var length = BitConverter.ToInt32(prefix);
        if (length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException($"Message length {length} is outside the allowed range.");

        var payload = new byte[length];
        var payloadBytes = await ReadAtMostAsync(stream, payload, cancellationToken);
        if (payloadBytes != length)
            throw new EndOfStreamException("Message payload was incomplete.");
        return JsonDocument.Parse(payload);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException($"Message length {payload.Length} is outside the allowed range.");

        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
