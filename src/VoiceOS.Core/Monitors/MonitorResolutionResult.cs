namespace VoiceOS.Core.Monitors;

public abstract record MonitorResolutionResult;

/// <summary>The target resolved to exactly one monitor.</summary>
public sealed record MonitorResolved(MonitorInfo Monitor) : MonitorResolutionResult;

/// <summary>No monitor in the topology matched the requested target.</summary>
public sealed record MonitorNotFound(string Reason) : MonitorResolutionResult;

/// <summary>Multiple monitors matched and no deterministic choice exists.</summary>
public sealed record MonitorAmbiguous(string Reason) : MonitorResolutionResult;

/// <summary>CurrentMonitor or a relative target was requested but no current-monitor context was provided.</summary>
public sealed record MonitorNoCurrentContext : MonitorResolutionResult;
