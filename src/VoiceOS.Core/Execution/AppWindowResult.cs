using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;

/// <summary>Result from IWindowAwareLauncher: execution status plus the concrete window that was opened or focused.</summary>
public sealed record AppWindowResult(
    ExecutionStatus Status,
    string? Detail = null,
    WindowCandidate? ResultWindow = null)
{
    public bool Succeeded => Status == ExecutionStatus.Success;

    public static AppWindowResult Ok(WindowCandidate? window, string? detail = null)
        => new(ExecutionStatus.Success, detail, window);

    public static AppWindowResult Fail(ExecutionStatus status, string detail)
        => new(status, detail);
}
