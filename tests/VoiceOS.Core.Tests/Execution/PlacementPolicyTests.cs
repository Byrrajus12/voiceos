using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Pure unit tests for PlacementPolicy. No Win32, no DPI context, no stubs needed.
/// All values are virtual-screen physical-pixel coordinates.
/// </summary>
public class PlacementPolicyTests
{
    // ── Helper factories ──────────────────────────────────────────────────────

    private static MonitorBounds Rect(int l, int t, int r, int b) => new(l, t, r, b);

    // A typical 1920x1080 monitor work area at left=0 (primary-like)
    private static MonitorBounds FHD => Rect(0, 0, 1920, 1040);           // 1920x1040 (taskbar subtracted)
    // A 2560x1440 work area at left=0 (high-res source)
    private static MonitorBounds QHD => Rect(0, 0, 2560, 1400);

    // ── ComputeNormalPlacement ────────────────────────────────────────────────

    [Fact]
    public void SameSize_WindowFitsExactly_PositionPreserved()
    {
        // Window at 25% from left, 10% from top of source.
        var window = Rect(480, 104, 1440, 624);    // 960x520 on FHD source
        var result = PlacementPolicy.ComputeNormalPlacement(window, FHD, FHD);

        // Same size — no shrink.
        Assert.Equal(960, result.Width);
        Assert.Equal(520, result.Height);
        // relX = 480/1920 = 0.25 → 0.25*1920 = 480 on same-size target
        Assert.Equal(480, result.Left);
        Assert.Equal(104, result.Top);
    }

    [Fact]
    public void SmallerTarget_WindowFits_NoShrink_RelativePositionPreserved()
    {
        // Window is 400x300, fits in a 800x600 target. Start at 25% / 25% of a 1600x1200 source.
        var source = Rect(0, 0, 1600, 1200);
        var target = Rect(0, 0, 800, 600);
        var window = Rect(400, 300, 800, 600);     // 400x300 at (25%, 25%) of source

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // Fits without shrink (400x300 ≤ 800x600)
        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
        // relX = 400/1600 = 0.25 → 0.25*800 = 200; relY = 300/1200 = 0.25 → 0.25*600 = 150
        Assert.Equal(200, result.Left);
        Assert.Equal(150, result.Top);
    }

    [Fact]
    public void WindowLargerThanTarget_ProportionalShrink_ToFit()
    {
        // 1920x1080 window moving from QHD to FHD — window is wider than target
        var window = Rect(0, 0, 1920, 1080);
        // source and target both 1920-wide but window is as tall as FHD (which is 1040 work area)
        var source = QHD;
        var target = FHD;   // 1920x1040

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // Window width = 1920 = target width, but height 1080 > target height 1040
        // scaleX = 1920/1920 = 1.0, scaleY = 1040/1080 ≈ 0.963; min = scaleY
        float scaleY = (float)1040 / 1080;
        int expectedW = Math.Max(1, (int)(1920 * scaleY));
        int expectedH = Math.Max(1, (int)(1080 * scaleY));

        Assert.Equal(expectedW, result.Width);
        Assert.Equal(expectedH, result.Height);
        // Stays within target
        Assert.True(result.Right <= target.Right);
        Assert.True(result.Bottom <= target.Bottom);
    }

    [Fact]
    public void WindowMuchLargerThanTarget_ProportionalShrink_BothDimensions()
    {
        // 2000x1500 window → 800x600 target: both dimensions overflow
        var source = Rect(0, 0, 2560, 1440);
        var target = Rect(0, 0, 800, 600);
        var window = Rect(0, 0, 2000, 1500);

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        float scaleX = (float)800 / 2000;   // 0.40
        float scaleY = (float)600 / 1500;   // 0.40
        float scale  = Math.Min(scaleX, scaleY);   // 0.40
        int expectedW = Math.Max(1, (int)(2000 * scale));
        int expectedH = Math.Max(1, (int)(1500 * scale));

        Assert.Equal(expectedW, result.Width);
        Assert.Equal(expectedH, result.Height);
        Assert.True(result.Width <= target.Width);
        Assert.True(result.Height <= target.Height);
    }

    [Fact]
    public void NoUpscale_WindowSmallerThanTarget_SizeUnchanged()
    {
        // 300x200 window on a 400x300 source, target is huge 3840x2160 — must not grow
        var source = Rect(0, 0, 400, 300);
        var target = Rect(0, 0, 3840, 2160);
        var window = Rect(50, 75, 350, 275);   // 300x200

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // No upscale
        Assert.Equal(300, result.Width);
        Assert.Equal(200, result.Height);
    }

    [Fact]
    public void RelativePosition_TopLeft_PreservedOnTarget()
    {
        // Window snapped to top-left of source → top-left of target
        var source = Rect(0, 0, 1920, 1080);
        var target = Rect(1920, 0, 3840, 1080);   // right monitor
        var window = Rect(0, 0, 960, 540);         // top-left quarter

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // relX = 0/1920 = 0 → target.Left + 0 = 1920
        // relY = 0/1080 = 0 → target.Top  + 0 = 0
        Assert.Equal(1920, result.Left);
        Assert.Equal(0, result.Top);
        Assert.Equal(960, result.Width);
        Assert.Equal(540, result.Height);
    }

    [Fact]
    public void RelativePosition_BottomRight_PreservedOnTarget()
    {
        // Window in bottom-right quadrant of source
        var source = Rect(0, 0, 1920, 1080);
        var target = Rect(1920, 0, 3840, 1080);
        var window = Rect(960, 540, 1920, 1080);   // bottom-right 960x540

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // relX = 960/1920 = 0.5 → 1920 + 0.5*1920 = 2880
        // relY = 540/1080 = 0.5 → 0   + 0.5*1080 = 540
        Assert.Equal(2880, result.Left);
        Assert.Equal(540, result.Top);
        Assert.Equal(960, result.Width);
        Assert.Equal(540, result.Height);
    }

    [Fact]
    public void Clamping_WindowPositionExceedsTarget_ClampedToFitInside()
    {
        // Window at far right of source — its relative position on a smaller target
        // would put it outside the target bounds; it must be clamped inside.
        var source = Rect(0, 0, 1920, 1080);
        var target = Rect(0, 0, 800, 600);
        var window = Rect(1800, 1000, 1920, 1080);  // 120x80 near bottom-right corner

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // Window should be entirely inside target
        Assert.True(result.Left >= target.Left);
        Assert.True(result.Top  >= target.Top);
        Assert.True(result.Right  <= target.Right);
        Assert.True(result.Bottom <= target.Bottom);
    }

    [Fact]
    public void NegativeTargetCoordinates_LeftOfPrimary_CalculatesCorrectly()
    {
        // Monitor to the left of primary (like a laptop dock scenario)
        var source = Rect(0, 0, 1920, 1080);           // primary
        var target = Rect(-1280, 0, 0, 1024);           // 1280x1024 left monitor
        var window = Rect(0, 0, 960, 540);              // top-left quadrant

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // Placement should be inside the negative-coordinate target
        Assert.True(result.Left >= target.Left);
        Assert.True(result.Top  >= target.Top);
        Assert.True(result.Right  <= target.Right);
        Assert.True(result.Bottom <= target.Bottom);
        // No shrink needed (960x540 fits in 1280x1024)
        Assert.Equal(960, result.Width);
        Assert.Equal(540, result.Height);
    }

    [Fact]
    public void TaskbarReducedWorkArea_WindowClampedWithinWorkArea()
    {
        // Target has taskbar at bottom — work area is 1920x1040, not 1920x1080
        var source = Rect(0, 0, 1920, 1040);
        var target = Rect(1920, 0, 3840, 1040);   // same work area on right monitor
        var window = Rect(0, 960, 1920, 1040);     // maximally-tall strip at bottom

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        Assert.True(result.Bottom <= target.Bottom,
            $"Bottom {result.Bottom} exceeds target work area {target.Bottom}");
        Assert.True(result.Top >= target.Top);
    }

    [Fact]
    public void ZeroWidthSource_DoesNotThrow_UsesZeroRelativePosition()
    {
        // Degenerate: sourceWorkArea.Width = 0 — must not divide by zero
        var source = Rect(0, 0, 0, 1080);
        var target = Rect(0, 0, 1920, 1080);
        var window = Rect(0, 100, 400, 500);

        var result = PlacementPolicy.ComputeNormalPlacement(window, source, target);

        // relX defaults to 0 → left = target.Left = 0; size unchanged (400x400 fits)
        Assert.Equal(0, result.Left);
        Assert.Equal(400, result.Width);
    }

    // ── WorkspaceToScreen / ScreenToWorkspace ─────────────────────────────────

    [Fact]
    public void WorkspaceToScreen_ZeroOriginPrimaryWorkArea_IsNoOp()
    {
        // When primary work area starts at (0,0), workspace coords = screen coords
        var primaryWorkArea = Rect(0, 0, 1920, 1040);
        var workspaceRect   = Rect(100, 50, 500, 300);

        var screen = PlacementPolicy.WorkspaceToScreen(workspaceRect, primaryWorkArea);

        Assert.Equal(workspaceRect, screen);
    }

    [Fact]
    public void WorkspaceToScreen_NonZeroOrigin_AppliesOffset()
    {
        // Primary work area offset by 40px from top (taskbar at top)
        var primaryWorkArea = Rect(0, 40, 1920, 1080);
        var workspaceRect   = Rect(100, 50, 500, 300);

        var screen = PlacementPolicy.WorkspaceToScreen(workspaceRect, primaryWorkArea);

        // dx=0, dy=40
        Assert.Equal(100, screen.Left);
        Assert.Equal(90,  screen.Top);
        Assert.Equal(500, screen.Right);
        Assert.Equal(340, screen.Bottom);
    }

    [Fact]
    public void ScreenToWorkspace_RoundTrip_IsIdentity()
    {
        var primaryWorkArea = Rect(0, 40, 1920, 1080);
        var original = Rect(200, 200, 800, 600);

        var workspace = PlacementPolicy.ScreenToWorkspace(original, primaryWorkArea);
        var restored  = PlacementPolicy.WorkspaceToScreen(workspace, primaryWorkArea);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void WorkspaceToScreen_NegativeOrigin_LeftOfPrimary()
    {
        // Rare: primary work area starts at negative coords
        var primaryWorkArea = Rect(-10, 0, 1910, 1080);
        var workspaceRect   = Rect(100, 50, 500, 300);

        var screen = PlacementPolicy.WorkspaceToScreen(workspaceRect, primaryWorkArea);

        // dx = -10
        Assert.Equal(90,  screen.Left);
        Assert.Equal(50,  screen.Top);
        Assert.Equal(490, screen.Right);
        Assert.Equal(300, screen.Bottom);
    }
}
