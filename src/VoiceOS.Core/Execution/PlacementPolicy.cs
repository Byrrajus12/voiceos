using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Pure, stateless placement policy for moving a window from one monitor to another.
///
/// All inputs and outputs are virtual-screen physical-pixel coordinates.
/// No Win32 calls — fully testable without a Windows environment.
/// </summary>
public static class PlacementPolicy
{
    /// <summary>
    /// Computes the new window rect when moved from <paramref name="sourceWorkArea"/> to
    /// <paramref name="targetWorkArea"/>, preserving the window's relative position.
    ///
    /// Size policy:
    ///   - If the window fits in the target work area: size is unchanged.
    ///   - If the window is too large: scaled down proportionally until it fits.
    ///   - Window is never scaled UP even if the target is larger.
    ///
    /// Position policy:
    ///   - Relative position within source work area is preserved on the target.
    ///   - Final rect is clamped to remain fully inside the target work area.
    ///   - Negative virtual-screen coordinates are supported (monitors left/above primary).
    /// </summary>
    public static MonitorBounds ComputeNormalPlacement(
        MonitorBounds windowRect,
        MonitorBounds sourceWorkArea,
        MonitorBounds targetWorkArea)
    {
        int w = windowRect.Width;
        int h = windowRect.Height;

        // Relative position of window top-left within the source work area.
        // May be outside [0,1] if window is partially off-screen — clamping handles it.
        float relX = sourceWorkArea.Width > 0
            ? (float)(windowRect.Left - sourceWorkArea.Left) / sourceWorkArea.Width
            : 0f;
        float relY = sourceWorkArea.Height > 0
            ? (float)(windowRect.Top - sourceWorkArea.Top) / sourceWorkArea.Height
            : 0f;

        // Apply same relative position on target work area.
        int left = targetWorkArea.Left + (int)Math.Round(relX * targetWorkArea.Width);
        int top  = targetWorkArea.Top  + (int)Math.Round(relY * targetWorkArea.Height);

        // Proportional shrink only when the window is too large for the target.
        // Never upscale.
        if (w > targetWorkArea.Width || h > targetWorkArea.Height)
        {
            float scaleX = (float)targetWorkArea.Width  / w;
            float scaleY = (float)targetWorkArea.Height / h;
            float scale  = Math.Min(scaleX, scaleY);
            w = Math.Max(1, (int)(w * scale));
            h = Math.Max(1, (int)(h * scale));
        }

        // Clamp fully inside target work area.
        if (left + w > targetWorkArea.Right)  left = targetWorkArea.Right  - w;
        if (top  + h > targetWorkArea.Bottom) top  = targetWorkArea.Bottom - h;
        if (left < targetWorkArea.Left)       left = targetWorkArea.Left;
        if (top  < targetWorkArea.Top)        top  = targetWorkArea.Top;

        return new MonitorBounds(left, top, left + w, top + h);
    }

    /// <summary>
    /// Converts workspace-relative coordinates (as stored in WINDOWPLACEMENT.rcNormalPosition)
    /// to virtual-screen coordinates.
    ///
    /// Windows workspace origin is the top-left corner of the primary monitor's work area.
    /// For the common case (taskbar at bottom, primary work area starts at 0,0) this is a no-op.
    /// </summary>
    public static MonitorBounds WorkspaceToScreen(MonitorBounds workspaceRect, MonitorBounds primaryWorkArea)
    {
        int dx = primaryWorkArea.Left;
        int dy = primaryWorkArea.Top;
        return new MonitorBounds(
            workspaceRect.Left + dx,
            workspaceRect.Top  + dy,
            workspaceRect.Right  + dx,
            workspaceRect.Bottom + dy);
    }

    /// <summary>Inverse of <see cref="WorkspaceToScreen"/>.</summary>
    public static MonitorBounds ScreenToWorkspace(MonitorBounds screenRect, MonitorBounds primaryWorkArea)
    {
        int dx = primaryWorkArea.Left;
        int dy = primaryWorkArea.Top;
        return new MonitorBounds(
            screenRect.Left   - dx,
            screenRect.Top    - dy,
            screenRect.Right  - dx,
            screenRect.Bottom - dy);
    }
}
