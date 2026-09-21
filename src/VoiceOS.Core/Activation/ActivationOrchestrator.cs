using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using VoiceOS.Core.Speech;

namespace VoiceOS.Core.Activation;

/// <summary>
/// Bridges GlobalKeyboardHook events → ActivationStateMachine → audio capture effects.
///
/// Threading model:
///   - Hook events (OnKeyDown/OnKeyUp) arrive on the WH_KEYBOARD_LL hook thread.
///     Windows removes the hook if the callback blocks for ~300ms. Both methods
///     capture the timestamp and offload all work to Task.Run immediately.
///   - Grace timer (OnGraceTimerExpired) already fires on a thread-pool thread.
///   - State transitions and synchronous effects are serialized under _stateLock.
///   - The async pipeline (STT + Jev + execution) runs OUTSIDE the lock on a background task.
///   - StateChanged is always invoked outside _stateLock to prevent lock-order issues.
/// </summary>
public sealed class ActivationOrchestrator : IDisposable
{
    private readonly GlobalKeyboardHook _hook;
    private readonly AudioCaptureService _audio;
    private readonly RecordingDebugWriter? _debugWriter;
    private readonly ISpeechRecognizer? _speechRecognizer;
    private readonly IDecisionEngine? _decisionEngine;
    private readonly ProgramExecutor? _programExecutor;
    private readonly IAppCatalog? _catalog;
    private readonly IDisplayTopologyService? _topoService;
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
        ILogger<ActivationOrchestrator> logger,
        ISpeechRecognizer? speechRecognizer = null,
        IDecisionEngine? decisionEngine = null,
        ProgramExecutor? programExecutor = null,
        IAppCatalog? catalog = null,
        IDisplayTopologyService? topoService = null)
    {
        _hook = hook;
        _audio = audio;
        _debugWriter = debugWriter;
        _config = config;
        _logger = logger;
        _speechRecognizer = speechRecognizer;
        _decisionEngine = decisionEngine;
        _programExecutor = programExecutor;
        _catalog = catalog;
        _topoService = topoService;

        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
    }

    public void Start() => _hook.Install();

    private void OnKeyDown(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
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
        PipelineInput? pipeline = null;

        lock (_stateLock)
        {
            var (next, actions) = ActivationStateMachine.Transition(
                _ctx, evt, _config.ActivationMode,
                _config.GracePeriod, _config.HoldThreshold, now);
            _ctx = next;
            pipeline = ExecuteActions(actions, now);
            newState = _ctx.State;
        }

        StateChanged?.Invoke(this, newState);

        if (pipeline != null)
            _ = RunPipelineAsync(pipeline);
    }

    private void OnGraceTimerExpired(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        ActivationState newState;
        PipelineInput? pipeline = null;

        lock (_stateLock)
        {
            var (next, actions) = ActivationStateMachine.Transition(
                _ctx, ActivationEvent.GraceTimerExpired, _config.ActivationMode,
                _config.GracePeriod, _config.HoldThreshold, now);
            _ctx = next;
            pipeline = ExecuteActions(actions, now);
            newState = _ctx.State;
        }

        StateChanged?.Invoke(this, newState);

        if (pipeline != null)
            _ = RunPipelineAsync(pipeline);
    }

    // Called inside _stateLock. Returns PipelineInput when a recording was finalized.
    private PipelineInput? ExecuteActions(IReadOnlyList<ActivationAction> actions, DateTimeOffset now)
    {
        PipelineInput? pipeline = null;

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
                    }
                    else
                    {
                        _logger.LogInformation("Recording started");
                    }
                    break;

                case ActivationAction.StopRecording:
                    _stopActivationTime = now;
                    pipeline = FinalizeRecordingSync(now);
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

        return pipeline;
    }

    // Called inside _stateLock. Stops capture and saves debug WAV; returns pipeline input for async processing.
    private PipelineInput? FinalizeRecordingSync(DateTimeOffset stopTime)
    {
        _graceTimer?.Dispose();
        _graceTimer = null;

        _logger.LogInformation("Recording stopped, finalizing...");

        var (pcm, format) = _audio.StopCaptureAndGetPcm();

        var metadata = new RecordingMetadata(
            ActivationPressTime: _activationPressTime,
            RecordingStart: _recordingStartTime,
            MicCaptureStarted: _micCaptureStartedTime,
            StopActivationTime: _stopActivationTime,
            CaptureStopped: stopTime,
            WavFinalized: DateTimeOffset.UtcNow,
            TotalDurationSeconds: (stopTime - _recordingStartTime).TotalSeconds);

        if (_debugWriter != null && _config.DebugOutputEnabled && pcm.Length > 0)
        {
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

        if (pcm.Length == 0) return null;

        return new PipelineInput(pcm, format, metadata, DateTimeOffset.UtcNow);
    }

    private async Task RunPipelineAsync(PipelineInput input)
    {
        var pipelineStart = input.PipelineStart;
        TranscriptionResult? transcription = null;
        DecisionResult? decision = null;
        ProgramResult? programResult = null;
        DateTimeOffset? sttStart = null, sttEnd = null;
        DateTimeOffset? jevStart = null, jevEnd = null;

        try
        {
            // ── STT ──────────────────────────────────────────────────────────────────
            StateChanged?.Invoke(this, ActivationState.Transcribing);

            if (_speechRecognizer != null)
            {
                sttStart = DateTimeOffset.UtcNow;
                try
                {
                    var samples = PcmToFloatConverter.Convert16BitPcmToFloat(input.Pcm);
                    transcription = await _speechRecognizer.TranscribeAsync(samples.AsMemory())
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "STT error");
                }
                sttEnd = DateTimeOffset.UtcNow;
            }

            if (transcription == null || !transcription.Success || string.IsNullOrWhiteSpace(transcription.Transcript))
            {
                var reason = transcription?.Error ?? (_speechRecognizer == null ? "No STT service" : "Empty transcript");
                _logger.LogInformation("Skipping Jev — {Reason}", reason);
                return;
            }

            _logger.LogInformation("Transcript: \"{Text}\" ({SttMs:F0}ms STT)",
                transcription.Transcript, transcription.TranscriptionDurationMs);

            // ── JEV ──────────────────────────────────────────────────────────────────
            StateChanged?.Invoke(this, ActivationState.Understanding);
            DecisionState? decisionState = null;

            if (_decisionEngine != null)
            {
                // --- Decision prep: timed sub-stages ---
                var windowsSw = Stopwatch.StartNew();
                var windows = CandidateBuilder.GetOpenWindowsWithTimings(out var winTimings);
                windowsSw.Stop();

                var appsSw = Stopwatch.StartNew();
                var apps = _catalog != null
                    ? CandidateBuilder.GetInstalledApps(_catalog)
                    : (IReadOnlyList<AppCandidate>)[];
                appsSw.Stop();

                var topoSw = Stopwatch.StartNew();
                var topology = _topoService?.CaptureTopology() ?? DisplayTopology.Empty;
                topoSw.Stop();

                var stateSw = Stopwatch.StartNew();
                var foreground = CandidateBuilder.GetForegroundAppName();
                decisionState = new DecisionState(
                    transcription.Transcript, foreground, apps, windows,
                    [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
                    [SnapDirection.Left, SnapDirection.Right],
                    topology);
                stateSw.Stop();

                _logger.LogInformation(
                    "Decision prep: windows={WindowsMs:F0}ms (enum={EnumMs:F0}ms proc={ProcMs:F0}ms aumid={AumidMs:F0}ms hit={CacheHits} miss={CacheMisses}) apps={AppsMs:F0}ms topo={TopoMs:F0}ms state={StateMs:F0}ms",
                    windowsSw.ElapsedMilliseconds, winTimings.EnumerateMs, winTimings.ProcessMs, winTimings.AumidMs,
                    winTimings.CacheHits, winTimings.CacheMisses,
                    appsSw.ElapsedMilliseconds, topoSw.ElapsedMilliseconds, stateSw.ElapsedMilliseconds);

                jevStart = DateTimeOffset.UtcNow;
                try
                {
                    decision = await _decisionEngine.DecideAsync(decisionState).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Decision engine error");
                }
                jevEnd = DateTimeOffset.UtcNow;
            }

            // ── EXECUTION ─────────────────────────────────────────────────────────────
            // Prefer the typed VoiceProgram from the decision engine.
            // Single-plan fallback applies ONLY when compound was not detected (unit_N_* answers absent);
            // if compound was attempted but TryBuildProgram returned null, fail silently rather than
            // partially executing the first action of a failed compound command.
            if (_programExecutor != null && decision != null && decisionState != null)
            {
                bool compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var cmpAns)
                    && cmpAns.QuestionType == "noul"
                    && cmpAns.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;

                var program = decision.Program
                    ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));

                if (program != null)
                {
                    var execSw = Stopwatch.StartNew();
                    try
                    {
                        programResult = await _programExecutor
                            .ExecuteAsync(program, decisionState.OpenWindows, decisionState.Topology)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Execution error for program ({Steps} steps)",
                            program.Steps.Count);
                    }
                    execSw.Stop();
                    _logger.LogInformation("Execution={ExecMs:F0}ms", execSw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            var pipelineEnd = DateTimeOffset.UtcNow;
            var result = new PipelineResult(
                input.Metadata, transcription, decision, decision?.Plan,
                pipelineStart, sttStart, sttEnd, jevStart, jevEnd, pipelineEnd, programResult);

            LogPipelineSummary(result);
            StateChanged?.Invoke(this, ActivationState.Idle);
        }
    }

    private void LogPipelineSummary(PipelineResult r)
    {
        var totalMs = (r.PipelineEnd - r.PipelineStart).TotalMilliseconds;
        var sttMs = r.SttStart.HasValue && r.SttEnd.HasValue
            ? (r.SttEnd.Value - r.SttStart.Value).TotalMilliseconds : 0;
        var jevMs = r.JevStart.HasValue && r.JevEnd.HasValue
            ? (r.JevEnd.Value - r.JevStart.Value).TotalMilliseconds : 0;

        var action = r.Plan?.Action.ToString() ?? "none";
        var confidence = r.Plan?.Confidence ?? 0;
        var transcript = r.Transcription?.Transcript ?? "(none)";
        var execStatus = r.ProgramExecution == null ? "-"
            : r.ProgramExecution.AllSucceeded
                ? $"ok({r.ProgramExecution.ExecutedCount})"
                : $"fail({r.ProgramExecution.FirstFailure?.Status})";

        _logger.LogInformation(
            "Pipeline: speech={SpeechMs:F0}ms STT={SttMs:F0}ms Jev={JevMs:F0}ms total={TotalMs:F0}ms | \"{Transcript}\" → {Action} ({Confidence:P0}) exec={ExecStatus}",
            r.Recording.TotalDurationSeconds * 1000, sttMs, jevMs, totalMs, transcript, action, confidence, execStatus);
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
        _speechRecognizer?.Dispose();
    }

    private sealed record PipelineInput(byte[] Pcm, WaveFormat Format, RecordingMetadata Metadata, DateTimeOffset PipelineStart);
}
