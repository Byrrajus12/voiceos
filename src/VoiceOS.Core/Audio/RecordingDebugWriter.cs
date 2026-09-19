using System.Text.Json;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace VoiceOS.Core.Audio;

/// <summary>
/// Saves debug recordings to %LOCALAPPDATA%\VoiceOS\recordings\ as {timestamp}.wav + {timestamp}.json.
/// </summary>
public sealed class RecordingDebugWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _outputDirectory;
    private readonly ILogger<RecordingDebugWriter> _logger;

    public RecordingDebugWriter(ILogger<RecordingDebugWriter> logger)
    {
        _logger = logger;
        _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceOS", "recordings");
        Directory.CreateDirectory(_outputDirectory);
        _logger.LogDebug("Debug recordings will be saved to {Dir}", _outputDirectory);
    }

    public void Save(byte[] pcmData, WaveFormat format, RecordingMetadata metadata)
    {
        var baseName = metadata.ActivationPressTime.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
        var wavPath = Path.Combine(_outputDirectory, $"{baseName}.wav");
        var jsonPath = Path.Combine(_outputDirectory, $"{baseName}.json");

        using (var writer = new WaveFileWriter(wavPath, format))
            writer.Write(pcmData, 0, pcmData.Length);

        var finalMetadata = metadata with { WavFinalized = DateTimeOffset.UtcNow };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(finalMetadata, JsonOptions));

        _logger.LogInformation("Saved {Wav} ({Seconds:F2}s)", wavPath, metadata.TotalDurationSeconds);
    }
}
