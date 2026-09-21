using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Monitors;

public class MonitorResolverTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static MonitorInfo Monitor(
        int left, int top, int right, int bottom,
        bool isPrimary = false,
        DisplayConnectionKind connectionKind = DisplayConnectionKind.External,
        string? gdiName = null,
        nint hMonitor = 0) => new(
        HMonitor: hMonitor != 0 ? hMonitor : (nint)(Math.Abs(left) + right + top + bottom + 1),
        Bounds:   new MonitorBounds(left, top, right, bottom),
        WorkArea: new MonitorBounds(left, top, right, bottom),
        IsPrimary: isPrimary,
        GdiDeviceName: gdiName ?? $"\\\\.\\DISPLAY_{left}_{top}",
        DevicePath: null,
        FriendlyName: null,
        ConnectionKind: connectionKind);

    // Two-monitor layout matching the dev machine topology:
    //   internal laptop panel left, external MSI G27C5 primary right.
    private static readonly MonitorInfo LaptopInternal =
        Monitor(-2560, 0, 0, 1440,
            isPrimary: false,
            connectionKind: DisplayConnectionKind.Internal,
            gdiName: "\\\\.\\DISPLAY1",
            hMonitor: 0x20001);
    private static readonly MonitorInfo ExternalPrimary =
        Monitor(0, 0, 1920, 1080,
            isPrimary: true,
            connectionKind: DisplayConnectionKind.External,
            gdiName: "\\\\.\\DISPLAY2",
            hMonitor: 0x10062);

    private static DisplayTopology TwoMonitors    => new([LaptopInternal, ExternalPrimary]);
    private static DisplayTopology SingleExternal => new([ExternalPrimary]);
    private static DisplayTopology EmptyTopology  => DisplayTopology.Empty;

    private static MonitorResolutionResult Resolve(
        MonitorTarget target,
        DisplayTopology? topology = null,
        MonitorInfo? current = null)
        => MonitorResolver.Resolve(target, topology ?? TwoMonitors, current);

    // ── PrimaryMonitor ────────────────────────────────────────────────────────

    [Fact]
    public void Primary_NormalSuccess()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new PrimaryMonitor()));
        Assert.Equal(ExternalPrimary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Primary_MissingPrimary_NotFound()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, isPrimary: false, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, isPrimary: false, gdiName: "\\\\.\\DISPLAY2")]);
        Assert.IsType<MonitorNotFound>(Resolve(new PrimaryMonitor(), t));
    }

    [Fact]
    public void Primary_EmptyTopology_NotFound()
    {
        Assert.IsType<MonitorNotFound>(Resolve(new PrimaryMonitor(), EmptyTopology));
    }

    // ── CurrentMonitor ────────────────────────────────────────────────────────

    [Fact]
    public void Current_WithContext_Resolved()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new CurrentMonitor(), current: ExternalPrimary));
        Assert.Equal(ExternalPrimary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Current_WithoutContext_NoCurrentContext()
    {
        Assert.IsType<MonitorNoCurrentContext>(Resolve(new CurrentMonitor(), current: null));
    }

    // ── OtherMonitor ──────────────────────────────────────────────────────────

    [Fact]
    public void Other_TwoMonitors_ReturnsTheOther()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new OtherMonitor(), current: ExternalPrimary));
        Assert.Equal(LaptopInternal.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Other_TwoMonitors_FromInternalReturnsExternal()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new OtherMonitor(), current: LaptopInternal));
        Assert.Equal(ExternalPrimary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Other_OneMonitor_NotFound()
    {
        Assert.IsType<MonitorNotFound>(Resolve(new OtherMonitor(), SingleExternal, ExternalPrimary));
    }

    [Fact]
    public void Other_ThreePlusMonitors_Ambiguous()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, isPrimary: true,  gdiName: "\\\\.\\DISPLAY1", hMonitor: 1),
            Monitor(1920, 0, 3840, 1080,                   gdiName: "\\\\.\\DISPLAY2", hMonitor: 2),
            Monitor(3840, 0, 5760, 1080,                   gdiName: "\\\\.\\DISPLAY3", hMonitor: 3)]);
        var current = t.Monitors[0];
        Assert.IsType<MonitorAmbiguous>(Resolve(new OtherMonitor(), t, current));
    }

    [Fact]
    public void Other_WithoutContext_NoCurrentContext()
    {
        Assert.IsType<MonitorNoCurrentContext>(Resolve(new OtherMonitor(), current: null));
    }

    // ── InternalMonitor ───────────────────────────────────────────────────────

    [Fact]
    public void Internal_OneInternal_Resolved()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new InternalMonitor()));
        Assert.Equal(LaptopInternal.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Internal_None_NoUnknowns_NotFound()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.External, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.External, gdiName: "\\\\.\\DISPLAY2")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new InternalMonitor(), t));
        Assert.DoesNotContain("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Internal_None_WithUnknowns_NotFoundMentionsUnknown()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.External, gdiName: "\\\\.\\DISPLAY2")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new InternalMonitor(), t));
        Assert.Contains("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Internal_Multiple_Ambiguous()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.Internal, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.Internal, gdiName: "\\\\.\\DISPLAY2")]);
        Assert.IsType<MonitorAmbiguous>(Resolve(new InternalMonitor(), t));
    }

    // ── ExternalMonitor ───────────────────────────────────────────────────────

    [Fact]
    public void External_OneExternal_Resolved()
    {
        var r = Assert.IsType<MonitorResolved>(Resolve(new ExternalMonitor()));
        Assert.Equal(ExternalPrimary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void External_None_NoUnknowns_NotFound()
    {
        var t = new DisplayTopology([
            Monitor(0, 0, 2560, 1440, connectionKind: DisplayConnectionKind.Internal, gdiName: "\\\\.\\DISPLAY1")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new ExternalMonitor(), t));
        Assert.DoesNotContain("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void External_None_WithUnknowns_NotFoundMentionsUnknown()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.Unknown,  gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.Internal, gdiName: "\\\\.\\DISPLAY2")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new ExternalMonitor(), t));
        Assert.Contains("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void External_Multiple_Ambiguous()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.External, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.External, gdiName: "\\\\.\\DISPLAY2")]);
        Assert.IsType<MonitorAmbiguous>(Resolve(new ExternalMonitor(), t));
    }

    [Fact]
    public void External_UnknownDoesNotCountAsExternal()
    {
        // One Unknown, no confirmed External — must not resolve to the Unknown monitor.
        var t = new DisplayTopology([
            Monitor(0, 0, 1920, 1080,
                connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY1")]);
        Assert.IsType<MonitorNotFound>(Resolve(new ExternalMonitor(), t));
    }

    [Fact]
    public void Internal_UnknownDoesNotCountAsInternal()
    {
        // One Unknown, no confirmed Internal — must not resolve to the Unknown monitor.
        var t = new DisplayTopology([
            Monitor(0, 0, 1920, 1080,
                connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY1")]);
        Assert.IsType<MonitorNotFound>(Resolve(new InternalMonitor(), t));
    }

    // ── Relative: Left ────────────────────────────────────────────────────────

    [Fact]
    public void Relative_Left_StandardHorizontal()
    {
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Left), current: ExternalPrimary));
        Assert.Equal(LaptopInternal.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Relative_Left_NoCandidates_NotFound()
    {
        Assert.IsType<MonitorNotFound>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Left), current: LaptopInternal));
    }

    // ── Relative: Right ───────────────────────────────────────────────────────

    [Fact]
    public void Relative_Right_StandardHorizontal()
    {
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Right), current: LaptopInternal));
        Assert.Equal(ExternalPrimary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Relative_Right_NoCandidates_NotFound()
    {
        Assert.IsType<MonitorNotFound>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Right), current: ExternalPrimary));
    }

    // ── Relative: Above / Below ───────────────────────────────────────────────

    [Fact]
    public void Relative_Above_VerticalLayout()
    {
        var top    = Monitor(0, 0,    1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var bottom = Monitor(0, 1080, 1920, 2160,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var t = new DisplayTopology([top, bottom]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Above), t, bottom));
        Assert.Equal(top.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Relative_Below_VerticalLayout()
    {
        var top    = Monitor(0, 0,    1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var bottom = Monitor(0, 1080, 1920, 2160,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var t = new DisplayTopology([top, bottom]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Below), t, top));
        Assert.Equal(bottom.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    // ── Negative virtual-screen coordinates ───────────────────────────────────

    [Fact]
    public void Relative_Left_NegativeCoordinates()
    {
        var primary   = Monitor(0,     0, 1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var secondary = Monitor(-1920, 0,    0, 1080,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var t = new DisplayTopology([primary, secondary]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Left), t, primary));
        Assert.Equal(secondary.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Relative_Above_NegativeCoordinates()
    {
        var primary = Monitor(0,     0, 1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var above   = Monitor(0, -1080, 1920,    0,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var t = new DisplayTopology([primary, above]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Above), t, primary));
        Assert.Equal(above.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    // ── Diagonal / offset: nearest by squared distance ────────────────────────

    [Fact]
    public void Relative_Right_DiagonalOffset_NearestWins()
    {
        // current center=(960,540). near center=(2880,740): sq=1920²+200²=3726400.
        // far center=(5960,540): sq=5000²=25000000. near wins.
        var current = Monitor(0,    0, 1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var near    = Monitor(1920, 200, 3840, 1280,                gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var far     = Monitor(5000, 0,  6920, 1080,                 gdiName: "\\\\.\\DISPLAY3", hMonitor: 3);
        var t = new DisplayTopology([current, near, far]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Right), t, current));
        Assert.Equal(near.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    [Fact]
    public void Relative_Left_DiagonalOffset_NearestWins()
    {
        var current  = Monitor(0,     0, 1920, 1080, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var nearLeft = Monitor(-1920, 0,    0, 1080,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var farLeft  = Monitor(-5000, 0, -3000, 1080,                 gdiName: "\\\\.\\DISPLAY3", hMonitor: 3);
        var t = new DisplayTopology([current, nearLeft, farLeft]);
        var r = Assert.IsType<MonitorResolved>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Left), t, current));
        Assert.Equal(nearLeft.GdiDeviceName, r.Monitor.GdiDeviceName);
    }

    // ── Exact squared-distance tie → MonitorAmbiguous ────────────────────────

    [Fact]
    public void Relative_ExactTie_Ambiguous()
    {
        // current center=(50,50).
        // ca center=(150,100): sq=(100²+50²)=12500.
        // cb center=(150,0):   sq=(100²+50²)=12500. Exact integer tie → Ambiguous.
        var current = Monitor(0,    0,   100, 100, isPrimary: true, gdiName: "\\\\.\\DISPLAY1", hMonitor: 1);
        var ca      = Monitor(100,  50,  200, 150,                  gdiName: "\\\\.\\DISPLAY2", hMonitor: 2);
        var cb      = Monitor(100, -50,  200,  50,                  gdiName: "\\\\.\\DISPLAY3", hMonitor: 3);
        var t = new DisplayTopology([current, ca, cb]);
        Assert.IsType<MonitorAmbiguous>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Right), t, current));
    }

    // ── No current context for relative/other targets ─────────────────────────

    [Fact]
    public void Relative_WithoutContext_NoCurrentContext()
    {
        Assert.IsType<MonitorNoCurrentContext>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Left), current: null));
    }

    [Fact]
    public void Relative_EmptyTopology_NotFound()
    {
        var orphan = Monitor(0, 0, 1920, 1080, gdiName: "\\\\.\\DISPLAY_ORPHAN", hMonitor: 99);
        Assert.IsType<MonitorNotFound>(
            Resolve(new RelativeMonitor(RelativeMonitorDirection.Right), EmptyTopology, orphan));
    }

    // ── Topology / model edge cases ───────────────────────────────────────────

    [Fact]
    public void Topology_FriendlyNameMissing_StillValid()
    {
        var m = new MonitorInfo(1,
            new MonitorBounds(0, 0, 1920, 1080),
            new MonitorBounds(0, 0, 1920, 1080),
            IsPrimary: true,
            GdiDeviceName: "\\\\.\\DISPLAY1",
            DevicePath: null,
            FriendlyName: null,
            ConnectionKind: DisplayConnectionKind.Internal);
        var t = new DisplayTopology([m]);
        Assert.IsType<MonitorResolved>(Resolve(new PrimaryMonitor(), t));
    }

    [Fact]
    public void Topology_InternalNoFriendlyName_ResolvedAsInternal()
    {
        var m = new MonitorInfo(1,
            new MonitorBounds(0, 0, 2560, 1440),
            new MonitorBounds(0, 0, 2560, 1440),
            IsPrimary: false,
            GdiDeviceName: "\\\\.\\DISPLAY1",
            DevicePath: null,
            FriendlyName: null,
            ConnectionKind: DisplayConnectionKind.Internal);
        var t = new DisplayTopology([m]);
        var r = Assert.IsType<MonitorResolved>(Resolve(new InternalMonitor(), t));
        Assert.Equal(DisplayConnectionKind.Internal, r.Monitor.ConnectionKind);
        Assert.Null(r.Monitor.FriendlyName);
    }

    [Fact]
    public void Topology_NegativeWorkArea_RoundTrips()
    {
        var m = new MonitorInfo(1,
            new MonitorBounds(-2560, -100, 0, 1340),
            new MonitorBounds(-2560, -100, 0, 1340),
            IsPrimary: false,
            GdiDeviceName: "\\\\.\\DISPLAY1",
            DevicePath: null,
            FriendlyName: null,
            ConnectionKind: DisplayConnectionKind.Unknown);
        Assert.Equal(-2560, m.WorkArea.Left);
        Assert.Equal(-100,  m.WorkArea.Top);
        Assert.Equal(2560,  m.WorkArea.Width);
    }

    [Fact]
    public void Topology_HMonitorIsSnapshotOnly_NotComputedFromBounds()
    {
        nint handle = 0x1234ABCD;
        var m = new MonitorInfo(handle,
            new MonitorBounds(0, 0, 1920, 1080),
            new MonitorBounds(0, 0, 1920, 1080),
            true, "\\\\.\\DISPLAY1", null, null, DisplayConnectionKind.External);
        Assert.Equal(handle, m.HMonitor);
    }

    // ── Unknown classification safety ─────────────────────────────────────────

    [Fact]
    public void ConnectionKind_UnknownOnly_InternalNotFound_MentionsUnknown()
    {
        var t = new DisplayTopology([
            Monitor(0,    0, 1920, 1080, connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY1"),
            Monitor(1920, 0, 3840, 1080, connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY2")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new InternalMonitor(), t));
        Assert.Contains("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectionKind_UnknownOnly_ExternalNotFound_MentionsUnknown()
    {
        var t = new DisplayTopology([
            Monitor(0, 0, 1920, 1080, connectionKind: DisplayConnectionKind.Unknown, gdiName: "\\\\.\\DISPLAY1")]);
        var result = Assert.IsType<MonitorNotFound>(Resolve(new ExternalMonitor(), t));
        Assert.Contains("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
