namespace VoiceOS.Core.Monitors;

public interface IDisplayTopologyService
{
    /// <summary>Captures the current active display topology from Windows.</summary>
    DisplayTopology CaptureTopology();

    /// <summary>
    /// Returns the snapshot entry that currently contains the given window handle.
    /// Returns null if the window cannot be located in the provided topology.
    /// </summary>
    MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology);

    /// <summary>
    /// Returns the monitor containing the current foreground window.
    /// Returns null if there is no foreground window or it cannot be matched in the topology.
    /// </summary>
    MonitorInfo? GetForegroundMonitor(DisplayTopology topology);
}
