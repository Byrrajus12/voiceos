namespace VoiceOS.Core.Execution;

public interface IAppLauncher
{
    ExecutionResult Launch(string? candidateId);
}
