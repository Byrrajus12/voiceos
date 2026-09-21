using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;

public enum VoiceStepStatus { Success, TargetResolutionFailed, ExecutionFailed }

public sealed record VoiceStepResult(
    string StepId,
    VoiceStepStatus Status,
    WindowCandidate? ResultWindow = null,
    string? Message = null)
{
    public bool Succeeded => Status == VoiceStepStatus.Success;

    public static VoiceStepResult Ok(string stepId, WindowCandidate? window = null, string? message = null)
        => new(stepId, VoiceStepStatus.Success, window, message);

    public static VoiceStepResult ResolutionFailed(string stepId, string message)
        => new(stepId, VoiceStepStatus.TargetResolutionFailed, null, message);

    public static VoiceStepResult ExecutionFailed(string stepId, string message)
        => new(stepId, VoiceStepStatus.ExecutionFailed, null, message);
}

public sealed record ProgramResult(IReadOnlyList<VoiceStepResult> StepResults)
{
    public bool AllSucceeded => StepResults.All(r => r.Succeeded);
    public VoiceStepResult? FirstFailure => StepResults.FirstOrDefault(r => !r.Succeeded);
    public int ExecutedCount => StepResults.Count;
}
