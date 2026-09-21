using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Execution;

public interface IWindowMoveService
{
    /// <summary>
    /// Moves a window to the given monitor, preserving its semantic show state.
    ///
    /// <para>
    /// <b>hwnd</b> must be a concrete, non-zero window handle sampled by the caller
    /// before this call. Returns <see cref="ExecutionStatus.WindowStale"/> if the HWND
    /// is no longer valid at execution time.
    /// </para>
    ///
    /// <para>
    /// State preservation:
    ///   Maximized  → remains maximized on target monitor (using target's work area).
    ///   Minimized  → restore placement updated; window stays minimized.
    ///   Normal     → moved with relative-position preservation and proportional shrink if needed.
    /// </para>
    ///
    /// <para>
    /// Returns <see cref="ExecutionStatus.TopologyStale"/> if the target monitor is no longer
    /// active according to Windows at the moment of execution.
    /// </para>
    /// </summary>
    ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo targetMonitor, DisplayTopology topology);
}
