using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Controls system output volume via the Windows Core Audio API (IAudioEndpointVolume).
/// Works directly on the endpoint volume scalar; does not simulate key presses.
/// </summary>
public sealed class VolumeService : IVolumeService
{
    private const float StepFraction = 0.05f; // 5% per relative step

    private readonly ILogger<VolumeService> _logger;

    public VolumeService(ILogger<VolumeService> logger) => _logger = logger;

    public ExecutionResult SetVolume(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return Apply(scalar => clamped / 100f, $"Set volume to {clamped}%");
    }

    public ExecutionResult AdjustVolume(VolumeDirection direction)
    {
        float delta = direction == VolumeDirection.Up ? StepFraction : -StepFraction;
        return Apply(
            current => Math.Clamp(current + delta, 0f, 1f),
            $"Volume {direction} by {(int)(StepFraction * 100)}%");
    }

    private ExecutionResult Apply(Func<float, float> compute, string description)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var vol = device.AudioEndpointVolume;
            float before = vol.MasterVolumeLevelScalar;
            vol.MasterVolumeLevelScalar = compute(before);
            float after = vol.MasterVolumeLevelScalar;
            _logger.LogInformation("Volume: {Desc} ({Before:P0} → {After:P0})", description, before, after);
            return ExecutionResult.Ok(description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Volume operation failed: {Desc}", description);
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
    }

    /// <summary>Pure volume clamping calculation — usable in tests without Windows APIs.</summary>
    public static float ComputeAbsolute(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    public static float ComputeRelative(float current, VolumeDirection direction)
    {
        float delta = direction == VolumeDirection.Up ? StepFraction : -StepFraction;
        return Math.Clamp(current + delta, 0f, 1f);
    }
}
