using Microsoft.Extensions.Logging;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Config;

namespace VoiceOS.Core.Activation;

/// <summary>
/// Bridges GlobalKeyboardHook events → ActivationStateMachine → audio capture effects.
///
/// Threading model:
///   - Hook events (OnKeyDown/OnKeyUp) arrive on the WH_KEYBOARD_LL hook thread.
///     Windows removes the hook if the callback blocks for ~300ms. Both methods
///     capture the timestamp and offload all work to Task.Run immediately.
///   - Grace timer (OnGraceTimerExpired) already fires on a thread-pool thread.
///   - All state transitions and effects are serialized under _stateLock.
///   - StateChanged is always invoked outside _stateLock to prevent lock-order issues.
/// </summary>
public sealed class ActivationOrchestrator : IDisposable
{
    private readonly GlobalKeyboardHook _hook;
    private readonly AudioCaptureService _audio;
    private readonly RecordingDebugWriter? _debugWriter;
    private readonly VoiceOSConfig _config;
    private readonly ILogger<ActivationOrchestrator> _logger;

    private readonly object _stateLock = new();
    private MachineContext _ctx = MachineContext.Initial;
    private System.Threading.Timer? _graceTimer;
    private bool _disposed;

    // Timing state for debug metadata (accessed only under _stateLock)
    private DateTimeOffset _activationPressTime;
    private DateTimeOffset _recordingStartTime;
    private DateTimeOffset _micCaptureStartedTime;
    private DateTimeOffset _stopActivationTime;

    public event EventHandler<ActivationState>? StateChanged;

    public ActivationOrchestrator(
        GlobalKeyboardHook hook,
        AudioCaptureService audio,
        RecordingDebugWriter? debugWriter,
        VoiceOSConfig config,
        ILogger<ActivationOrchestrator> logger)
    {
        _hook = hook;
        _audio = audio;
        _debugWriter = debugWriter;
        _config = config;
        _logger = logger;

        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
    }

    public void Start() => _hook.Install();

    private void OnKeyDown(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        // Return immediately — hook callbacks must not block or Windows removes the hook.
        _ = Task.Run(() => ProcessEvent(ActivationEvent.KeyDown, now));
    }

    private void OnKeyUp(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        _ = Task.Run(() => ProcessEvent(ActivationEvent.KeyUp, now));
    }

    private void ProcessEvent(ActivationEvent evt, DateTimeOffset now)
    {
        ActivationState newState;

        lock (_stateLock)
        {
            var (next, actions) = ActivationStateMachine.Transition(
                _ctx, evt, _config.ActivationMode,
                _config.GracePeriod, _config.HoldThreshold, now);
            _ctx = next;
            ExecuteActions(actions, now);
            // Read state after ExecuteActions — it may have rolled back to Idle on mic failure.
            newState = _ctx.State;
        }

        StateChanged?.Invoke(this, newState);
    }

    private void OnGraceTimerExpired(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        ActivationState newState;

        lock (_stateLock)
        {
            var (next, actions) = ActivationStateMachine.Transition(
                _ctx, ActivationEvent.GraceTimerExpired, _config.ActivationMode,
                _config.GracePeriod, _config.HoldThreshold, now);
            _ctx = next;
            ExecuteActions(actions, now);
            newState = _ctx.State;
        }

        StateChanged?.Invoke(this, newState);
    }

    // Called inside _stateLock. Never raises StateChanged — callers read _ctx.State
    // after this method returns and fire StateChanged outside the lock.
    private void ExecuteActions(IReadOnlyList<ActivationAction> actions, DateTimeOffset now)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case ActivationAction.StartRecording:
                    _activationPressTime = now;
                    _recordingStartTime = now;
                    var started = _audio.StartCapture();
                    _micCaptureStartedTime = DateTimeOffset.UtcNow;
                    if (!started)
                    {
                        _logger.LogWarning("Mic capture failed — rolling back to Idle");
                        _ctx = MachineContext.Initial;
                        // _ctx.State is now Idle; caller reads it and fires StateChanged outside the lock.
                    }
                    else
                    {
                        _logger.LogInformation("Recording started");
                    }
                    break;

                case ActivationAction.StopRecording:
                    _stopActivationTime = now;
                    FinalizeRecording(now);
                    break;

                case ActivationAction.StartGraceTimer(var duration):
                    _graceTimer?.Dispose();
                    _graceTimer = new System.Threading.Timer(
                        OnGraceTimerExpired, null,
                        (int)duration.TotalMilliseconds, Timeout.Infinite);
                    break;

                case ActivationAction.CancelGraceTimer:
                    _graceTimer?.Dispose();
                    _graceTimer = null;
                    break;
            }
        }
    }

    // Called inside _stateLock.
    private void FinalizeRecording(DateTimeOffset stopTime)
    {
        _graceTimer?.Dispose();
        _graceTimer = null;

        _logger.LogInformation("Recording stopped, finalizing...");

        var (pcm, format) = _audio.StopCaptureAndGetPcm();

        if (_debugWriter != null && _config.DebugOutputEnabled && pcm.Length > 0)
        {
            var metadata = new RecordingMetadata(
                ActivationPressTime: _activationPressTime,
                RecordingStart: _recordingStartTime,
                MicCaptureStarted: _micCaptureStartedTime,
                StopActivationTime: _stopActivationTime,
                CaptureStopped: stopTime,
                WavFinalized: DateTimeOffset.UtcNow,
                TotalDurationSeconds: (stopTime - _recordingStartTime).TotalSeconds);

            try
            {
                _debugWriter.Save(pcm, format, metadata);
                _logger.LogInformation("Debug WAV saved ({Bytes} bytes PCM)", pcm.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save debug recording");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hook.KeyDown -= OnKeyDown;
        _hook.KeyUp -= OnKeyUp;
        _graceTimer?.Dispose();
        _hook.Dispose();
        _audio.Dispose();
    }
}
