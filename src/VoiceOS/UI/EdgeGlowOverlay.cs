using System.Drawing;
using Microsoft.Win32;

namespace VoiceOS.UI;

/// <summary>Owns the animation clock and one set of narrow layered windows per monitor.</summary>
internal sealed class EdgeGlowOverlay : IProductUiSurface
{
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _idleDelay = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1500 };
    private readonly List<GlowStripWindow> _strips = [];
    private readonly List<PerimeterGlowField> _fields = [];
    private ProductUiState _state;
    private long _lastFrameTime;
    private double _phase;
    private double _activityBlend;
    private bool _disposed;

    public EdgeGlowOverlay()
    {
        _frameTimer.Tick += (_, _) => RenderFrame();
        _idleDelay.Tick += (_, _) => Hide();
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void SetState(ProductUiState state)
    {
        if (_disposed) return;
        _idleDelay.Stop();

        // The activation machine briefly publishes Idle between stopping capture and
        // starting transcription. Allow that edge to settle without flashing off.
        if (state == ProductUiState.Idle && _state == ProductUiState.Listening)
        {
            _idleDelay.Start();
            return;
        }

        if (state == ProductUiState.Idle)
        {
            Hide();
            return;
        }

        if (_state == ProductUiState.Idle)
        {
            if (_strips.Count == 0 || _rebuildPending) BuildWindows();
            _lastFrameTime = Environment.TickCount64;
            _activityBlend = state == ProductUiState.Listening ? 0 : 1;
        }

        _state = state;
        RenderFrame();
        _frameTimer.Start();
        _topmostTimer.Start();
    }

    private void RenderFrame()
    {
        if (_state == ProductUiState.Idle) return;
        if (_rebuildPending) BuildWindows();
        long now = Environment.TickCount64;
        double elapsed = Math.Clamp((now - _lastFrameTime) / 1000.0, 0, 0.1);
        _lastFrameTime = now;
        double targetBlend = _state == ProductUiState.Listening ? 0 : 1;
        _activityBlend += Math.Clamp(targetBlend - _activityBlend, -elapsed / 0.22, elapsed / 0.22);
        // Understanding moves 30% faster; Listening retains the fuller field.
        _phase = (_phase + elapsed * (0.50 + 0.15 * _activityBlend)) % (2 * Math.PI);
        foreach (var field in _fields)
            field.Update(_phase, _activityBlend);
        foreach (var strip in _strips)
            strip.Render();
    }

    private void Hide()
    {
        _idleDelay.Stop();
        _frameTimer.Stop();
        _topmostTimer.Stop();
        _state = ProductUiState.Idle;
        foreach (var strip in _strips)
            strip.Hide();
    }

    private void BuildWindows()
    {
        _rebuildPending = false;
        foreach (var strip in _strips) strip.Dispose();
        _strips.Clear();
        _fields.Clear();

        foreach (var screen in Screen.AllScreens)
        {
            Rectangle b = screen.Bounds;
            int edge = Math.Min(Math.Min(b.Width, b.Height) / 5,
                Math.Clamp((int)Math.Round(46 * GlowStripWindow.DpiScaleAt(b)), 36, 96));
            if (edge < 1 || b.Width < 2 || b.Height < 2) continue;
            var field = new PerimeterGlowField(b, edge);
            _fields.Add(field);
            _strips.Add(new GlowStripWindow(new Rectangle(b.Left, b.Top, b.Width, edge), field));
            _strips.Add(new GlowStripWindow(new Rectangle(b.Left, b.Bottom - edge, b.Width, edge), field));
            if (b.Height > edge * 2)
            {
                _strips.Add(new GlowStripWindow(new Rectangle(b.Left, b.Top + edge, edge, b.Height - edge * 2), field));
                _strips.Add(new GlowStripWindow(new Rectangle(b.Right - edge, b.Top + edge, edge, b.Height - edge * 2), field));
            }
        }
    }

    private void ReassertTopmost()
    {
        if (_state == ProductUiState.Idle) return;
        foreach (var strip in _strips)
            strip.ReassertTopmost();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // SystemEvents can arrive on a different thread. The UI clock owns windows.
        _rebuildPending = true;
    }

    private volatile bool _rebuildPending;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _frameTimer.Stop();
        _topmostTimer.Stop();
        _idleDelay.Stop();
        _frameTimer.Dispose();
        _topmostTimer.Dispose();
        _idleDelay.Dispose();
        foreach (var strip in _strips) strip.Dispose();
        _strips.Clear();
        _fields.Clear();
    }
}
