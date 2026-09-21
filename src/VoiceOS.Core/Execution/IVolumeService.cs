using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

public interface IVolumeService
{
    ExecutionResult SetVolume(int percent);

    /// <summary>
    /// Adjust volume by a relative amount.
    /// When <paramref name="amount"/> is null, uses the service's default step.
    /// When set, adjusts by that many percentage points (e.g. 10 → ±10 points on a 0–100 scale).
    /// Result is clamped to [0, 1] scalar (0–100%).
    /// </summary>
    ExecutionResult AdjustVolume(VolumeDirection direction, int? amount = null);
}
