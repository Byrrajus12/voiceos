namespace VoiceOS.Core.Monitors;

/// <summary>
/// Runtime snapshot of one active display.
/// <para>
/// <b>HMonitor</b> is a runtime-only handle valid only for the lifetime of this snapshot.
/// Do not persist, serialize, or compare it across topology captures.
/// </para>
/// <para>
/// <b>GdiDeviceName</b> is the session-scoped join key between EnumDisplayMonitors and
/// DisplayConfig. Use it to correlate entries from different APIs within the same snapshot.
/// </para>
/// <para>
/// <b>DevicePath</b> is relatively stable physical-device metadata; may be useful later for
/// cross-session identity. May be null if DisplayConfig enrichment failed for this monitor.
/// </para>
/// <para>
/// <b>FriendlyName</b> is presentation metadata only; may be empty or null. An internal display
/// with no friendly name can later be surfaced as "Built-in Display" in the UX layer.
/// </para>
/// <para>
/// <b>ConnectionKind</b> is <see cref="DisplayConnectionKind.Unknown"/> when DisplayConfig
/// enrichment failed — absence of enrichment data does NOT imply External.
/// </para>
/// </summary>
public sealed record MonitorInfo(
    nint                HMonitor,
    MonitorBounds        Bounds,
    MonitorBounds        WorkArea,
    bool                 IsPrimary,
    string               GdiDeviceName,
    string?              DevicePath,
    string?              FriendlyName,
    DisplayConnectionKind ConnectionKind);
