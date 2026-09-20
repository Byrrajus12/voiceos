using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

public interface IMediaService
{
    ExecutionResult Send(MediaOperation op);
}
