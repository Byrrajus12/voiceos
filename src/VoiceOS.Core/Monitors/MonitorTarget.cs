namespace VoiceOS.Core.Monitors;

public abstract record MonitorTarget;

/// <summary>The monitor whose IsPrimary flag is true in the current topology.</summary>
public sealed record PrimaryMonitor : MonitorTarget;

/// <summary>The monitor containing the target window at execution time. Requires current-monitor context.</summary>
public sealed record CurrentMonitor : MonitorTarget;

/// <summary>
/// The one monitor that is not the current monitor.
/// Fails if zero or two-or-more other monitors exist.
/// </summary>
public sealed record OtherMonitor : MonitorTarget;

/// <summary>The monitor where IsInternalDisplay is true (laptop panel / built-in screen).</summary>
public sealed record InternalMonitor : MonitorTarget;

/// <summary>
/// The monitor where IsInternalDisplay is false (external display).
/// Fails if zero or multiple external monitors exist.
/// </summary>
public sealed record ExternalMonitor : MonitorTarget;

/// <summary>The nearest monitor in the given direction relative to the current monitor's center.</summary>
public sealed record RelativeMonitor(RelativeMonitorDirection Direction) : MonitorTarget;
