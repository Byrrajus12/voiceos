using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

public interface IVolumeService
{
    ExecutionResult SetVolume(int percent);
    ExecutionResult AdjustVolume(VolumeDirection direction);
}
