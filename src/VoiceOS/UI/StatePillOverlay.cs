using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VoiceOS.UI;

/// <summary>A single clock for the state object's geometry and the capsule's width.</summary>
internal sealed class StatePillOverlay : IDisposable
{
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 25 };
    private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1500 };
    private readonly Random _random = new();
    private readonly double[] _barHeight = new double[5];
    private readonly double[] _barTarget = new double[5];
    private readonly double[] _barDelay = new double[5];
    private readonly double[] _barResponse = [0.12, 0.16, 0.11, 0.18, 0.14];
    private StatePillWindow? _window;
    private ProductUiState _state;
    private ProductUiState _resultState = ProductUiState.Success;
    private string _clarification = "Which Chrome window?";
    private string _errorMessage = "Couldn't find that window";
    private string? _actingMessage;
    private double _width;
    private double _widthVelocity;
    private double _appearance;
    private double _listeningShape;
    private double _resultShape;
    private double _actingMotion;
    private double _clarifyShape;
    private double _phase;
    private double _energy = 0.8;
    private long _lastTick;
    private bool _disposed;
    private volatile bool _rebuildPending;

    public StatePillOverlay()
    {
        _frameTimer.Tick += (_, _) => Tick();
        _topmostTimer.Tick += (_, _) => _window?.ReassertTopmost();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        for (int i = 0; i < 5; i++)
        {
            _barHeight[i] = (new double[] { 0.36, 0.83, 0.53, 0.94, 0.43 })[i];
            _barTarget[i] = _barHeight[i];
            _barDelay[i] = 0.12 + i * 0.07;
        }
    }

    public void SetState(ProductUiState state)
    {
        if (_disposed) return;
        bool fromIdle = _state == ProductUiState.Idle && state != ProductUiState.Idle;
        if (_window is null && state != ProductUiState.Idle) BuildWindow();
        if (state is ProductUiState.Success or ProductUiState.Error) _resultState = state;
        if (fromIdle)
        {
            // A hidden pill starts in its requested geometry; only visible state changes morph.
            _listeningShape = state == ProductUiState.Listening ? 1 : 0;
            _resultShape = state is ProductUiState.Success or ProductUiState.Error ? 1 : 0;
            _actingMotion = state == ProductUiState.Acting ? 1 : 0;
            _clarifyShape = state is ProductUiState.Clarify or ProductUiState.Error
                || state == ProductUiState.Acting && _actingMessage is not null ? 1 : 0;
            string initialMessage = CurrentMessage(state);
            _width = _clarifyShape > 0
                ? _window!.ClarifyWidth(initialMessage) : _window!.CompactWidth;
            _widthVelocity = 0;
        }
        _state = state;
        _lastTick = Environment.TickCount64;
        if (state != ProductUiState.Idle) _topmostTimer.Start();
        else _topmostTimer.Stop();
        if (state != ProductUiState.Idle || _appearance > 0.005) _frameTimer.Start();
        Tick();
    }

    public void SetClarificationMessage(string message)
    {
        _clarification = string.IsNullOrWhiteSpace(message) ? "Which one did you mean?" : message.Trim();
        if (_state == ProductUiState.Clarify) _frameTimer.Start();
    }

    public void SetErrorMessage(string message)
    {
        _errorMessage = string.IsNullOrWhiteSpace(message) ? "Something went wrong" : message.Trim();
        if (_state == ProductUiState.Error) _frameTimer.Start();
    }

    public void SetActingMessage(string? message)
    {
        _actingMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (_state == ProductUiState.Acting) _frameTimer.Start();
    }

    private string CurrentMessage(ProductUiState state) => state switch
    {
        ProductUiState.Error => _errorMessage,
        ProductUiState.Clarify => _clarification,
        ProductUiState.Acting => _actingMessage ?? "",
        _ => ""
    };

    private void Tick()
    {
        if (_disposed || _window is null) return;
        if (_rebuildPending) BuildWindow();
        StatePillWindow window = _window!;
        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _lastTick) / 1000.0, 0, 0.08);
        _lastTick = now;
        bool shown = _state != ProductUiState.Idle;
        string message = CurrentMessage(_state);
        bool expanded = _state is ProductUiState.Clarify or ProductUiState.Error
            || _state == ProductUiState.Acting && _actingMessage is not null;
        double targetWidth = expanded
            ? window.ClarifyWidth(message) : window.CompactWidth;
        Spring(ref _width, ref _widthVelocity, targetWidth, dt, 16);
        _width = Math.Clamp(_width, window.CompactWidth, window.MaxWidth);
        _appearance = Approach(_appearance, shown ? 1 : 0, dt, shown ? 0.11 : 0.10);
        _listeningShape = Approach(_listeningShape, _state == ProductUiState.Listening ? 1 : 0, dt, 0.28);
        _resultShape = Approach(_resultShape,
            _state is ProductUiState.Success or ProductUiState.Error ? 1 : 0, dt, 0.30);
        _actingMotion = Approach(_actingMotion, _state == ProductUiState.Acting ? 1 : 0, dt, 0.37);
        _clarifyShape = Approach(_clarifyShape,
            expanded ? 1 : 0, dt, 0.28);
        // One integrated phase: Understanding 3.2 rad/s, Acting 5.6 rad/s.
        _phase += dt * (3.2 + 2.4 * _actingMotion);
        if (_state == ProductUiState.Listening) UpdateBars(dt);

        if (!shown && _appearance < 0.006)
        {
            window.Hide();
            _frameTimer.Stop();
            _width = window.CompactWidth;
            _widthVelocity = 0;
            _listeningShape = 0;
            _resultShape = 0;
            _actingMotion = 0;
            _clarifyShape = 0;
            _phase = 0;
            return;
        }

        var frame = new StatePillFrame(
            Math.Max(1, (int)Math.Round(_width)), _appearance, _listeningShape,
            _resultShape, _actingMotion, _clarifyShape, _phase,
            _state, _resultState, message, _barHeight, _energy);
        window.Render(in frame);
        if (_state is ProductUiState.Clarify or ProductUiState.Error &&
            Math.Abs(_width - targetWidth) < 0.15 && Math.Abs(_widthVelocity) < 0.2 &&
            Math.Abs(_appearance - 1) < 0.005 &&
            (_state != ProductUiState.Error || _resultShape > 0.995) &&
            _clarifyShape > 0.995)
            _frameTimer.Stop();
    }

    private void UpdateBars(double dt)
    {
        for (int i = 0; i < 5; i++)
        {
            _barDelay[i] -= dt;
            if (_barDelay[i] <= 0)
            {
                double next;
                int tries = 0;
                do
                {
                    next = 0.17 + _random.NextDouble() * 0.82;
                    tries++;
                } while (i > 0 && tries < 7 && Math.Abs(next - _barHeight[i - 1]) < 0.25);
                _barTarget[i] = next;
                _barDelay[i] = 0.25 + _random.NextDouble() * 0.42 + i * 0.013;
            }
            _barHeight[i] = Approach(_barHeight[i], _barTarget[i], dt, _barResponse[i]);
        }
    }

    private void BuildWindow()
    {
        _rebuildPending = false;
        _window?.Dispose();
        IntPtr foreground = GetForegroundWindow();
        Screen screen = foreground != IntPtr.Zero
            ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
        _window = new StatePillWindow(screen);
        _width = Math.Clamp(_width, _window.CompactWidth, _window.MaxWidth);
        if (_width == 0) _width = _window.CompactWidth;
    }

    private static double Approach(double current, double target, double dt, double tau)
        => current + (target - current) * (1 - Math.Exp(-dt / tau));

    private static void Spring(ref double value, ref double velocity, double target, double dt, double frequency)
    {
        double offset = value - target;
        double decay = Math.Exp(-frequency * dt);
        double step = (velocity + frequency * offset) * dt;
        value = target + (offset + step) * decay;
        velocity = (velocity - frequency * step) * decay;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _rebuildPending = true;

#if DEBUG
    public void SaveFrame(string path) => _window?.SaveFrame(path);
    public string DescribeFrame() => $"state={_state} width={_width:F1} shape={_listeningShape:F2}/{_resultShape:F2} bars={string.Join(',', _barHeight.Select(x => x.ToString("F2")))}";
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _frameTimer.Stop();
        _topmostTimer.Stop();
        _frameTimer.Dispose();
        _topmostTimer.Dispose();
        _window?.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
