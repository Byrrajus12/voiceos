namespace VoiceOS.Core.Monitors;

/// <summary>
/// Pure monitor resolver. Zero Win32 calls — operates solely on provided immutable snapshot data.
/// </summary>
public static class MonitorResolver
{
    public static MonitorResolutionResult Resolve(
        MonitorTarget target,
        DisplayTopology topology,
        MonitorInfo? currentMonitor)
        => target switch
        {
            PrimaryMonitor    => ResolvePrimary(topology),
            CurrentMonitor    => ResolveCurrentMonitor(currentMonitor),
            OtherMonitor      => ResolveOther(topology, currentMonitor),
            InternalMonitor   => ResolveByConnectionKind(topology, DisplayConnectionKind.Internal, "internal display"),
            ExternalMonitor   => ResolveByConnectionKind(topology, DisplayConnectionKind.External, "external monitor"),
            RelativeMonitor r => ResolveRelative(r.Direction, topology, currentMonitor),
            _                 => new MonitorNotFound($"Unknown target type: {target.GetType().Name}")
        };

    private static MonitorResolutionResult ResolvePrimary(DisplayTopology topology)
    {
        var match = topology.Monitors.Where(m => m.IsPrimary).ToList();
        return match.Count switch
        {
            1 => new MonitorResolved(match[0]),
            0 => new MonitorNotFound("No primary monitor in topology"),
            _ => new MonitorAmbiguous("Multiple primary monitors in topology")
        };
    }

    private static MonitorResolutionResult ResolveCurrentMonitor(MonitorInfo? current)
        => current is null ? new MonitorNoCurrentContext() : new MonitorResolved(current);

    private static MonitorResolutionResult ResolveOther(DisplayTopology topology, MonitorInfo? current)
    {
        if (current is null) return new MonitorNoCurrentContext();

        // Use HMonitor for same-topology exclusion — it is the runtime identity within one snapshot.
        var others = topology.Monitors.Where(m => m.HMonitor != current.HMonitor).ToList();

        return others.Count switch
        {
            1 => new MonitorResolved(others[0]),
            0 => new MonitorNotFound("No other monitor available"),
            _ => new MonitorAmbiguous($"{others.Count} other monitors present; cannot determine which one")
        };
    }

    private static MonitorResolutionResult ResolveByConnectionKind(
        DisplayTopology topology,
        DisplayConnectionKind kind,
        string label)
    {
        var matches  = topology.Monitors.Where(m => m.ConnectionKind == kind).ToList();
        var unknowns = topology.Monitors.Where(m => m.ConnectionKind == DisplayConnectionKind.Unknown).ToList();

        return matches.Count switch
        {
            1   => new MonitorResolved(matches[0]),
            > 1 => new MonitorAmbiguous($"{matches.Count} {label}s found"),
            // No confirmed match: distinguish between "definitely none" and "unknown classification"
            0 when unknowns.Count > 0
                => new MonitorNotFound($"No confirmed {label}; {unknowns.Count} display(s) have unknown connection classification"),
            _   => new MonitorNotFound($"No {label} in topology")
        };
    }

    private static MonitorResolutionResult ResolveRelative(
        RelativeMonitorDirection direction,
        DisplayTopology topology,
        MonitorInfo? current)
    {
        if (current is null) return new MonitorNoCurrentContext();

        int cx = current.Bounds.CenterX;
        int cy = current.Bounds.CenterY;

        var candidates = topology.Monitors
            .Where(m => m.HMonitor != current.HMonitor)
            .Where(m => IsInDirection(direction, cx, cy, m.Bounds.CenterX, m.Bounds.CenterY))
            .ToList();

        if (candidates.Count == 0)
            return new MonitorNotFound($"No monitor {direction} of current");

        if (candidates.Count == 1)
            return new MonitorResolved(candidates[0]);

        // Multiple candidates: pick nearest by squared integer center-to-center distance.
        // Using long to avoid overflow for extreme virtual desktop coordinates.
        static long SquaredDist(int cx, int cy, MonitorInfo m)
        {
            long dx = m.Bounds.CenterX - cx;
            long dy = m.Bounds.CenterY - cy;
            return dx * dx + dy * dy;
        }

        long minDist = candidates.Min(m => SquaredDist(cx, cy, m));
        var nearest  = candidates.Where(m => SquaredDist(cx, cy, m) == minDist).ToList();

        if (nearest.Count > 1)
            return new MonitorAmbiguous($"Multiple equidistant monitors {direction} of current");

        return new MonitorResolved(nearest[0]);
    }

    private static bool IsInDirection(RelativeMonitorDirection direction, int cx, int cy, int tx, int ty)
        => direction switch
        {
            RelativeMonitorDirection.Left  => tx < cx,
            RelativeMonitorDirection.Right => tx > cx,
            RelativeMonitorDirection.Above => ty < cy,
            RelativeMonitorDirection.Below => ty > cy,
            _ => false
        };
}
