using System.Drawing;

namespace VoiceOS.UI;

/// <summary>
/// One continuous color/opacity field for a monitor. Edge windows only crop it;
/// none owns a separate color phase or corner treatment.
/// </summary>
internal sealed class PerimeterGlowField
{
    private static readonly (int R, int G, int B)[] Spectrum =
    [
        (75, 200, 229), (73, 151, 244), (119, 122, 246),
        (188, 114, 226), (218, 139, 205), (156, 162, 246),
        (74, 186, 236)
    ];

    private readonly Rectangle _bounds;
    private readonly int _edge;
    private readonly int _widthSpan;
    private readonly int _heightSpan;
    private readonly int _perimeter;
    private readonly int[] _cornerWeight;
    private readonly int[] _falloff;
    private readonly int[] _core;
    private readonly int[] _colors;
    private readonly int[] _strength;

    public PerimeterGlowField(Rectangle bounds, int edge)
    {
        _bounds = bounds;
        _edge = edge;
        _widthSpan = bounds.Width - 1;
        _heightSpan = bounds.Height - 1;
        _perimeter = 2 * (_widthSpan + _heightSpan);
        _cornerWeight = new int[edge + 1];
        _falloff = new int[edge + 1];
        _core = new int[edge + 1];
        for (int d = 0; d <= edge; d++)
        {
            double t = (double)d / edge;
            double smooth = t * t * (3 - 2 * t);
            _cornerWeight[d] = d == edge ? 0 : Math.Max(1, (int)Math.Round(256 * (1 - smooth)));
            _falloff[d] = (int)Math.Round(255 * Math.Exp(-Math.Pow(t * 3.2, 1.55)));
            _core[d] = (int)Math.Round(35 * Math.Exp(-Math.Pow(d / Math.Max(1.0, edge * 0.075), 2)));
            if (d >= edge - 1) { _falloff[d] = 0; _core[d] = 0; }
        }
        _colors = new int[_perimeter];
        _strength = new int[_perimeter];
    }

    public void Update(double phase, double processingBlend)
    {
        double cycle = phase / (2 * Math.PI);
        for (int s = 0; s < _perimeter; s++)
        {
            double position = (double)s / _perimeter - cycle;
            double palette = position * Spectrum.Length;
            int index = (int)Math.Floor(palette);
            double t = palette - index;
            var p0 = Spectrum[Wrap(index - 1, Spectrum.Length)];
            var p1 = Spectrum[Wrap(index, Spectrum.Length)];
            var p2 = Spectrum[Wrap(index + 1, Spectrum.Length)];
            var p3 = Spectrum[Wrap(index + 2, Spectrum.Length)];

            int red = Spline(p0.R, p1.R, p2.R, p3.R, t);
            int green = Spline(p0.G, p1.G, p2.G, p3.G, t);
            int blue = Spline(p0.B, p1.B, p2.B, p3.B, t);
            // Processing keeps the same moving field but cools it gradually.
            red = (int)(red * (1 - 0.32 * processingBlend));
            green = (int)(green * (1 - 0.10 * processingBlend));
            _colors[s] = (red << 16) | (green << 8) | blue;

            double theta = 2 * Math.PI * position;
            double listening = 0.77 + 0.09 * Math.Sin(3 * theta);
            double processing = 0.58 + 0.23 * Math.Sin(3 * theta - phase);
            _strength[s] = (int)(160 * (listening + (processing - listening) * processingBlend));
        }
    }

    public (int PerimeterIndex, int Distance) Locate(int screenX, int screenY)
    {
        int x = screenX - _bounds.Left;
        int y = screenY - _bounds.Top;
        int horizontalDistance = Math.Min(y, _heightSpan - y);
        int verticalDistance = Math.Min(x, _widthSpan - x);
        int distance = Math.Min(horizontalDistance, verticalDistance);
        if (distance >= _edge) return (0, _edge);

        bool top = y <= _heightSpan / 2;
        bool left = x <= _widthSpan / 2;
        int horizontalPosition = top ? x : _widthSpan + _heightSpan + (_widthSpan - x);
        int verticalPosition = left
            ? 2 * _widthSpan + _heightSpan + (_heightSpan - y)
            : _widthSpan + y;
        // Unwrap the left projection at the top-left corner before blending.
        if (top && left) verticalPosition -= _perimeter;

        int horizontalWeight = _cornerWeight[Math.Min(horizontalDistance, _edge)];
        int verticalWeight = _cornerWeight[Math.Min(verticalDistance, _edge)];
        int coordinate = (int)Math.Round(
            (double)(horizontalPosition * horizontalWeight + verticalPosition * verticalWeight)
            / (horizontalWeight + verticalWeight));
        return (Wrap(coordinate, _perimeter), distance);
    }

    public int Pixel(int screenX, int screenY)
    {
        var (position, distance) = Locate(screenX, screenY);
        return PixelAt(position, distance);
    }

    public int PixelAt(int position, int distance)
    {
        if (distance >= _edge) return 0;
        int color = _colors[position];
        int alpha = Math.Clamp((_strength[position] * _falloff[distance] + 127) / 255 + _core[distance], 0, 225);
        int red = (((color >> 16) & 255) * alpha + 127) / 255;
        int green = (((color >> 8) & 255) * alpha + 127) / 255;
        int blue = ((color & 255) * alpha + 127) / 255;
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    private static int Spline(int a, int b, int c, int d, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;
        return Math.Clamp((int)Math.Round(0.5 * (2 * b + (c - a) * t
            + (2 * a - 5 * b + 4 * c - d) * t2
            + (-a + 3 * b - 3 * c + d) * t3)), 0, 255);
    }

    private static int Wrap(int value, int length) => ((value % length) + length) % length;
}
