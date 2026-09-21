namespace VoiceOS.Core.Monitors;

/// <summary>
/// Physical-pixel rectangle in virtual-screen coordinates.
/// Origin may be negative when monitors extend left of or above the primary.
/// </summary>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width   => Right - Left;
    public int Height  => Bottom - Top;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;
}
