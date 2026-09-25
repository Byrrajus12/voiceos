using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VoiceOS.UI;

internal readonly record struct StatePillFrame(
    int Width, double Opacity, double Listening, double Result, double Acting,
    double Clarify, double Phase, ProductUiState State, ProductUiState ResultState, string Message,
    double[] Bars, double Energy);

/// <summary>Smoked glass drawn into a premultiplied DIB on a click-through layered window.</summary>
internal sealed class StatePillWindow : NativeWindow, IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int ExLayered = 0x80000;
    private const int ExTopmost = 0x8;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Screen _screen;
    private readonly double _scale;
    private readonly int _top;
    private readonly int _height;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _previousBitmap;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly Font _font;
    private readonly StringFormat _textFormat;
    private readonly PointF[] _blobPoints = new PointF[64];
    private bool _visible;
    private bool _disposed;

    public int CompactWidth { get; }
    public int MaxWidth { get; }

    public StatePillWindow(Screen screen)
    {
        _screen = screen;
        _scale = GlowStripWindow.DpiScaleAt(screen.Bounds);
        _top = screen.WorkingArea.Top + (int)Math.Round(26 * _scale);
        _height = (int)Math.Round(50 * _scale);
        CompactWidth = (int)Math.Round(78 * _scale);
        MaxWidth = Math.Min((int)Math.Round(390 * _scale),
            Math.Max(CompactWidth, screen.WorkingArea.Width - (int)Math.Round(36 * _scale)));
        _font = new Font("Segoe UI Variable Display", (float)(15 * _scale),
            FontStyle.Regular, GraphicsUnit.Pixel);
        _textFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        _textFormat.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        _textFormat.Trimming = StringTrimming.EllipsisCharacter;

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = MaxWidth, Height = -_height, Planes = 1, BitCount = 32
            }
        };
        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_memoryDc, ref info, 0, out IntPtr pixels, IntPtr.Zero, 0);
        if (_memoryDc == IntPtr.Zero || _dib == IntPtr.Zero || pixels == IntPtr.Zero)
            throw new InvalidOperationException("Unable to allocate state pill bitmap.");
        _previousBitmap = SelectObject(_memoryDc, _dib);
        _bitmap = new Bitmap(MaxWidth, _height, MaxWidth * 4, PixelFormat.Format32bppPArgb, pixels);
        _graphics = Graphics.FromImage(_bitmap);
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        CreateHandle(new CreateParams
        {
            Caption = "VoiceOS State Pill",
            X = screen.Bounds.Left + (screen.Bounds.Width - CompactWidth) / 2,
            Y = _top, Width = CompactWidth, Height = _height,
            Style = WsPopup,
            ExStyle = ExLayered | ExTopmost | ExTransparent | ExToolWindow | ExNoActivate
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }

    public int ClarifyWidth(string message)
    {
        float measured = _graphics.MeasureString(message, _font, int.MaxValue, _textFormat).Width;
        return Math.Clamp((int)Math.Ceiling(100 * _scale + measured),
            CompactWidth, MaxWidth);
    }

    public void Render(in StatePillFrame frame)
    {
        int width = Math.Clamp(frame.Width, 1, MaxWidth);
        float scale = (float)_scale;
        _graphics.CompositingMode = CompositingMode.SourceCopy;
        _graphics.Clear(Color.Transparent);
        _graphics.CompositingMode = CompositingMode.SourceOver;

        float x = 2 * scale;
        float y = 2 * scale;
        float h = _height - 5 * scale;
        float w = width - 4 * scale;
        using (var shadow = Capsule(x, y + 1.6f * scale, w, h))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
            _graphics.FillPath(shadowBrush, shadow);
        using (var body = Capsule(x, y, w, h))
        {
            using var fill = new LinearGradientBrush(
                new RectangleF(x, y, w, h),
                Color.FromArgb(209, 39, 39, 40),
                Color.FromArgb(217, 26, 26, 28), 90f);
            _graphics.FillPath(fill, body);
            var saved = _graphics.Save();
            _graphics.SetClip(body);
            using (var upper = new LinearGradientBrush(
                new RectangleF(x, y, w, h * 0.56f),
                Color.FromArgb(17, 252, 250, 246), Color.Transparent, 90f))
                _graphics.FillRectangle(upper, x, y, w, h * 0.56f);
            using (var lower = new SolidBrush(Color.FromArgb(8, 3, 3, 4)))
                _graphics.FillRectangle(lower, x, y + h * 0.72f, w, h * 0.28f);
            _graphics.Restore(saved);
            using var rim = new Pen(Color.FromArgb(20, 244, 240, 232), 0.7f * scale);
            _graphics.DrawPath(rim, body);
        }

        // The compact object and the Clarify icon share a center until the capsule widens.
        float centerX = (float)(width / 2.0 +
            (39 * scale - width / 2.0) * frame.Clarify);
        float centerY = _height / 2f - 0.5f * scale;
        DrawStateObject(in frame, centerX, centerY, scale);
        if (frame.Clarify > 0.01)
        {
            float textX = 69 * scale;
            float textY = (_height - _font.GetHeight(_graphics)) / 2 - 1 * scale;
            var saved = _graphics.Save();
            _graphics.SetClip(new RectangleF(textX, 4 * scale,
                Math.Max(0, width - textX - 14 * scale), _height - 8 * scale));
            using var brush = new SolidBrush(Color.FromArgb(
                (int)Math.Round(229 * frame.Clarify), 239, 237, 232));
            _graphics.DrawString(frame.Message, _font, brush,
                new RectangleF(textX, textY,
                    Math.Max(0, width - textX - 14 * scale), _font.GetHeight(_graphics) + 3 * scale),
                _textFormat);
            _graphics.Restore(saved);
        }

        var dst = new PointNative(_screen.Bounds.Left + (_screen.Bounds.Width - width) / 2, _top);
        var size = new SizeNative(width, _height);
        var src = new PointNative(0, 0);
        var blend = new BlendFunction(0, 0,
            (byte)Math.Clamp((int)Math.Round(frame.Opacity * 255), 0, 255), 1);
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (!UpdateLayeredWindow(Handle, dc, ref dst, ref size,
                _memoryDc, ref src, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        if (!_visible) ReassertTopmost();
    }

    private void DrawStateObject(in StatePillFrame frame, float cx, float cy, float scale)
    {
        float listen = (float)frame.Listening;
        float result = (float)frame.Result;
        float blobOpacity = (1 - listen) * (1 - result) * (1 - (float)frame.Clarify);
        float fragmentWidth = (3.2f + 2.7f * (1 - listen)) * scale;
        if (listen > 0.005f)
        {
            using var barBrush = new SolidBrush(Color.FromArgb(
                (int)(213 * listen * (1 - result) * (1 - frame.Clarify)),
                224, 226, 225));
            for (int i = 0; i < 5; i++)
            {
                float horizontal = (i - 2) * (6.5f * listen + 2.1f * (1 - listen)) * scale;
                float barH = (float)((0.19 + 0.66 * frame.Bars[i] * frame.Energy) * 31 * scale);
                float height = barH * listen + 7 * scale * (1 - listen);
                using var shape = Capsule(cx + horizontal - fragmentWidth / 2,
                    cy - height / 2, fragmentWidth, Math.Max(fragmentWidth, height));
                _graphics.FillPath(barBrush, shape);
            }
        }

        if (blobOpacity > 0.005f)
        {
            float acting = (float)frame.Acting;
            float phase = (float)frame.Phase;
            float emerge = 0.62f + 0.38f * (1 - listen);
            float settle = 1 - 0.22f * result;
            float bodyScale = emerge * settle * scale;
            float centerShiftX = (0.65f * (float)Math.Sin(phase * 0.54f) +
                acting * 0.65f * (float)Math.Sin(phase * 0.41f)) * bodyScale;
            float centerShiftY = 0.55f * (float)Math.Sin(phase * 0.72f) * bodyScale;
            for (int i = 0; i < _blobPoints.Length; i++)
            {
                float angle = (float)(i * 2 * Math.PI / _blobPoints.Length);
                // Broad waves raise lobes; soft local dents pull the same outline inward.
                float lobe = 0.82f * (float)Math.Sin(2 * angle + phase * 0.43f) +
                    1.06f * (float)Math.Sin(3 * angle - phase * 0.72f);
                float dents =
                    (2.18f + 0.32f * (float)Math.Sin(phase * 0.37f)) *
                        SoftDent(angle, -1.25f + 0.16f * (float)Math.Sin(phase * 0.41f), 0.46f) +
                    (2.02f + 0.30f * (float)Math.Sin(phase * 0.47f + 1.3f)) *
                        SoftDent(angle, 0.72f + 0.18f * (float)Math.Sin(phase * 0.33f), 0.47f) +
                    (0.78f + 1.18f * (0.5f + 0.5f * (float)Math.Sin(phase * 0.51f))) *
                        SoftDent(angle, 2.80f + 0.12f * (float)Math.Sin(phase * 0.52f), 0.50f);
                float fourthRegion = (0.55f + 0.16f * (float)Math.Sin(phase * 0.39f + 0.8f)) *
                    SoftDent(angle, 2.02f + 0.13f * (float)Math.Sin(phase * 0.36f), 0.62f);
                float purpose = acting * (0.58f * (float)Math.Cos(angle) +
                    0.32f * (float)Math.Sin(2 * angle - phase * 0.67f));
                float deformation = lobe + fourthRegion - dents +
                    0.35f * (float)Math.Sin(phase * 1.05f);
                float radius = Math.Clamp(12.3f + (deformation < 0 ? 1.2f : 1f) * deformation + purpose,
                    7.1f, 17.8f) * bodyScale;
                _blobPoints[i] = new PointF(
                    cx + centerShiftX + radius * (float)Math.Cos(angle),
                    cy + centerShiftY + radius * (float)Math.Sin(angle));
            }
            using var silhouette = new GraphicsPath();
            silhouette.AddClosedCurve(_blobPoints, 0.34f);
            using var fill = new LinearGradientBrush(
                new RectangleF(cx - 15 * scale, cy - 15 * scale, 30 * scale, 30 * scale),
                Color.FromArgb((int)(216 * blobOpacity), 220, 225, 222),
                Color.FromArgb((int)(159 * blobOpacity), 154, 169, 170), 90f);
            _graphics.FillPath(fill, silhouette);
        }

        if (frame.Clarify > 0.01 && frame.State == ProductUiState.Clarify)
        {
            using var dot = new SolidBrush(Color.FromArgb((int)(176 * frame.Clarify),
                218, 207, 188));
            _graphics.FillEllipse(dot, cx - 2 * scale, cy - 2 * scale,
                4 * scale, 4 * scale);
        }
        if (result > 0.01f)
        {
            bool success = frame.ResultState == ProductUiState.Success;
            using var pen = new Pen(Color.FromArgb((int)(222 * result),
                success ? 186 : 207, success ? 214 : 177, success ? 192 : 174),
                2.15f * scale)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (success)
            {
                _graphics.DrawLines(pen, [
                    new PointF(cx - 7 * scale, cy),
                    new PointF(cx - 2 * scale, cy + 4.5f * scale),
                    new PointF(cx + 8 * scale, cy - 6 * scale)]);
            }
            else
            {
                _graphics.DrawLine(pen, cx - 5 * scale, cy - 5 * scale,
                    cx + 5 * scale, cy + 5 * scale);
                _graphics.DrawLine(pen, cx + 5 * scale, cy - 5 * scale,
                    cx - 5 * scale, cy + 5 * scale);
            }
        }
    }

    private static float SoftDent(float angle, float center, float width)
    {
        float delta = (float)Math.Atan2(Math.Sin(angle - center), Math.Cos(angle - center)) / width;
        return (float)Math.Exp(-0.5f * delta * delta);
    }

    private static GraphicsPath Capsule(float x, float y, float width, float height)
    {
        var path = new GraphicsPath();
        float d = Math.Min(width, height);
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

#if DEBUG
    public void SaveFrame(string path) => _bitmap.Save(path, ImageFormat.Png);
#endif

    public void ReassertTopmost()
    {
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0,
            (uint)(0x0001 | 0x0002 | 0x0010 | (_visible ? 0 : 0x0040)));
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(Handle, 0);
        _visible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
        _graphics.Dispose();
        _bitmap.Dispose();
        _font.Dispose();
        _textFormat.Dispose();
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_dib);
        DeleteDC(_memoryDc);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction(byte op, byte flags, byte alpha, byte format)
    { public byte BlendOp = op, BlendFlags = flags, SourceConstantAlpha = alpha, AlphaFormat = format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref PointNative dst,
        ref SizeNative size, IntPtr srcDc, ref PointNative src, uint key, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
