namespace VoiceOS.Core.Execution;

public enum ExecutionStatus
{
    Success,
    NoAction,
    AppNotFound,
    WindowNotFound,
    WindowStale,
    PlatformError
}

public record ExecutionResult(ExecutionStatus Status, string? Detail = null)
{
    public static ExecutionResult Ok(string? detail = null) => new(ExecutionStatus.Success, detail);
    public static ExecutionResult Noop(string reason) => new(ExecutionStatus.NoAction, reason);
    public static ExecutionResult Fail(ExecutionStatus status, string detail) => new(status, detail);
}
