using System.Drawing;
using System.Runtime.InteropServices;

namespace VoiceOS.UI;

/// <summary>A persistent premultiplied-alpha DIB in a topmost, input-transparent window.</summary>
internal sealed class GlowStripWindow : NativeWindow, IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int ExLayered = 0x80000;
    private const int ExTopmost = 0x8;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x84;
    private const int WmMouseActivate = 0x21;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Rectangle _bounds;
    private readonly PerimeterGlowField _field;
    private readonly int[] _perimeterIndex;
    private readonly byte[] _distance;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _previousBitmap;
    private readonly IntPtr _pixels;
    private bool _visible;
    private bool _disposed;

    public GlowStripWindow(Rectangle bounds, PerimeterGlowField field)
    {
        _bounds = bounds;
        _field = field;
        _perimeterIndex = new int[bounds.Width * bounds.Height];
        _distance = new byte[_perimeterIndex.Length];
        for (int y = 0; y < bounds.Height; y++)
        {
            int row = y * bounds.Width;
            for (int x = 0; x < bounds.Width; x++)
            {
                var (position, distance) = field.Locate(bounds.Left + x, bounds.Top + y);
                _perimeterIndex[row + x] = position;
                _distance[row + x] = (byte)distance;
            }
        }

        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bounds.Width,
                Height = -bounds.Height, // top-down BGRA
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };
        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_memoryDc, ref bitmapInfo, 0, out _pixels, IntPtr.Zero, 0);
        if (_memoryDc == IntPtr.Zero || _dib == IntPtr.Zero || _pixels == IntPtr.Zero)
            throw new InvalidOperationException("Unable to allocate edge glow bitmap.");
        _previousBitmap = SelectObject(_memoryDc, _dib);

        CreateHandle(new CreateParams
        {
            Caption = "VoiceOS Edge Glow",
            X = bounds.Left,
            Y = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Style = WsPopup,
            ExStyle = ExLayered | ExTopmost | ExTransparent | ExToolWindow | ExNoActivate
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == WmMouseActivate) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }

    public unsafe void Render()
    {
        int* pixels = (int*)_pixels;
        for (int i = 0; i < _perimeterIndex.Length; i++)
            pixels[i] = _field.PixelAt(_perimeterIndex[i], _distance[i]);

        var dst = new PointNative(_bounds.Left, _bounds.Top);
        var size = new SizeNative(_bounds.Width, _bounds.Height);
        var src = new PointNative(0, 0);
        var blend = new BlendFunction(0, 0, 255, 1);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            if (!UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    _memoryDc, ref src, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }

        if (!_visible)
        {
            ReassertTopmost();
        }
    }

    public void ReassertTopmost()
    {
        if (!_visible)
        {
            if (!SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _visible = true;
            return;
        }
        if (!SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
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
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_dib);
        DeleteDC(_memoryDc);
    }

    public static double DpiScaleAt(Rectangle bounds)
    {
        var center = new PointNative(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        IntPtr monitor = MonitorFromPoint(center, 2);
        return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint x, out _) == 0
            ? x / 96.0 : 1.0;
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
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
