using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Dictation;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Monitors;
using VoiceOS.Core.Speech;

namespace VoiceOS.Core.Activation;

/// <summary>
/// Owns the mutually exclusive command and literal-dictation capture lanes.
/// The Phase 1 command path remains STT -> typed VoiceProgram -> ProgramExecutor.
/// Dictation stops after STT and inserts the literal transcript into the exact
/// foreground target captured on the activation edge.
/// </summary>
public sealed class ActivationOrchestrator : IDisposable, IApplicationInteractionStateSource
{
    private readonly GlobalKeyboardHook _commandHook;
    private readonly GlobalKeyboardHook _dictationHook;
    private readonly AudioCaptureService _audio;
    private readonly RecordingDebugWriter? _debugWriter;
    private readonly ISpeechRecognizer? _speechRecognizer;
    private readonly IDecisionEngine? _decisionEngine;
    private readonly ProgramExecutor? _programExecutor;
    private readonly IAppCatalog? _catalog;
    private readonly IDisplayTopologyService? _topoService;
    private readonly IForegroundWindowService? _foregroundWindow;
    private readonly ITextInsertionService? _textInsertion;
    private readonly VoiceOSConfig _config;
    private readonly ILogger<ActivationOrchestrator> _logger;

    private readonly object _stateLock = new();
    private MachineContext _commandContext = MachineContext.Initial;
    private MachineContext _dictationContext = MachineContext.Initial;
    private InteractionKind? _activeCapture;
    private TextInsertionTarget _dictationTarget;
    private System.Threading.Timer? _graceTimer;
    private bool _disposed;

    private DateTimeOffset _activationPressTime;
    private DateTimeOffset _recordingStartTime;
    private DateTimeOffset _micCaptureStartedTime;
    private DateTimeOffset _stopActivationTime;

    public event EventHandler<ActivationState>? StateChanged;
    public event EventHandler<InteractionStateChanged>? InteractionChanged;
    public event EventHandler<ApplicationInteractionSnapshotEventArgs>? InteractionSnapshotChanged;

    public ActivationOrchestrator(
        GlobalKeyboardHook commandHook,
        GlobalKeyboardHook dictationHook,
        AudioCaptureService audio,
        RecordingDebugWriter? debugWriter,
        VoiceOSConfig config,
        ILogger<ActivationOrchestrator> logger,
        ISpeechRecognizer? speechRecognizer = null,
        IDecisionEngine? decisionEngine = null,
        ProgramExecutor? programExecutor = null,
        IAppCatalog? catalog = null,
        IDisplayTopologyService? topoService = null,
        IForegroundWindowService? foregroundWindow = null,
        ITextInsertionService? textInsertion = null)
    {
        _commandHook = commandHook;
        _dictationHook = dictationHook;
        _audio = audio;
        _debugWriter = debugWriter;
        _config = config;
        _logger = logger;
        _speechRecognizer = speechRecognizer;
        _decisionEngine = decisionEngine;
        _programExecutor = programExecutor;
        _catalog = catalog;
        _topoService = topoService;
        _foregroundWindow = foregroundWindow;
        _textInsertion = textInsertion;

        _commandHook.KeyDown += OnCommandKeyDown;
        _commandHook.KeyUp += OnCommandKeyUp;
        _dictationHook.KeyDown += OnDictationKeyDown;
        _dictationHook.KeyUp += OnDictationKeyUp;
    }

    public void Start()
    {
        _commandHook.Install();
        _dictationHook.Install();
    }

    private void OnCommandKeyDown(object? sender, EventArgs e)
        => QueueEvent(InteractionKind.Command, ActivationEvent.KeyDown, DateTimeOffset.UtcNow);

    private void OnCommandKeyUp(object? sender, EventArgs e)
        => QueueEvent(InteractionKind.Command, ActivationEvent.KeyUp, DateTimeOffset.UtcNow);

    private void OnDictationKeyDown(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var targetAtActivation = _foregroundWindow?.Capture() ?? default;
        QueueEvent(InteractionKind.Dictation, ActivationEvent.KeyDown, now, targetAtActivation);
    }

    private void OnDictationKeyUp(object? sender, EventArgs e)
        => QueueEvent(InteractionKind.Dictation, ActivationEvent.KeyUp, DateTimeOffset.UtcNow);

    private void QueueEvent(
        InteractionKind kind,
        ActivationEvent evt,
        DateTimeOffset now,
        TextInsertionTarget targetAtActivation = default)
        => _ = Task.Run(() => ProcessEvent(kind, evt, now, targetAtActivation));

    private void ProcessEvent(
        InteractionKind kind,
        ActivationEvent evt,
        DateTimeOffset now,
        TextInsertionTarget targetAtActivation = default)
    {
        ActivationState newState;
        PipelineInput? pipeline;

        lock (_stateLock)
        {
            if (_activeCapture is { } active && active != kind)
            {
                _logger.LogDebug("Ignoring {Kind} activation while {Active} owns audio capture", kind, active);
                return;
            }

            var (next, actions) = ActivationStateMachine.Transition(
                GetContext(kind), evt, GetMode(kind),
                _config.GracePeriod, _config.HoldThreshold, now);
            SetContext(kind, next);
            pipeline = ExecuteActions(kind, actions, now, targetAtActivation);
            newState = GetContext(kind).State;
        }

        PublishState(kind, newState);
        if (pipeline is not null)
            _ = RunPipelineAsync(pipeline);
    }

    private void OnGraceTimerExpired(object? state)
    {
        if (state is not InteractionKind kind)
            return;

        var now = DateTimeOffset.UtcNow;
        ActivationState newState;
        PipelineInput? pipeline;
        lock (_stateLock)
        {
            var (next, actions) = ActivationStateMachine.Transition(
                GetContext(kind), ActivationEvent.GraceTimerExpired, GetMode(kind),
                _config.GracePeriod, _config.HoldThreshold, now);
            SetContext(kind, next);
            pipeline = ExecuteActions(kind, actions, now);
            newState = GetContext(kind).State;
        }

        PublishState(kind, newState);
        if (pipeline is not null)
            _ = RunPipelineAsync(pipeline);
    }

    private PipelineInput? ExecuteActions(
        InteractionKind kind,
        IReadOnlyList<ActivationAction> actions,
        DateTimeOffset now,
        TextInsertionTarget targetAtActivation = default)
    {
        PipelineInput? pipeline = null;
        foreach (var action in actions)
        {
            switch (action)
            {
                case ActivationAction.StartRecording:
                    _activeCapture = kind;
                    if (kind == InteractionKind.Dictation)
                    {
                        _dictationTarget = targetAtActivation;
                        if (!_dictationTarget.IsValid)
                        {
                            _logger.LogWarning("Dictation activation has no valid foreground target");
                            SetContext(kind, MachineContext.Initial);
                            _activeCapture = null;
                            break;
                        }
                    }

                    _activationPressTime = now;
                    _recordingStartTime = now;
                    var started = _audio.StartCapture();
                    _micCaptureStartedTime = DateTimeOffset.UtcNow;
                    if (!started)
                    {
                        _logger.LogWarning("Mic capture failed — rolling back to Idle");
                        SetContext(kind, MachineContext.Initial);
                        _activeCapture = null;
                        _dictationTarget = default;
                    }
                    else
                    {
                        _logger.LogInformation("{Kind} recording started", kind);
                    }
                    break;

                case ActivationAction.StopRecording:
                    _stopActivationTime = now;
                    pipeline = FinalizeRecordingSync(kind, now);
                    _activeCapture = null;
                    break;

                case ActivationAction.StartGraceTimer(var duration):
                    _graceTimer?.Dispose();
                    _graceTimer = new System.Threading.Timer(
                        OnGraceTimerExpired, kind, (int)duration.TotalMilliseconds, Timeout.Infinite);
                    break;

                case ActivationAction.CancelGraceTimer:
                    _graceTimer?.Dispose();
                    _graceTimer = null;
                    break;
            }
        }
        return pipeline;
    }

    private PipelineInput? FinalizeRecordingSync(InteractionKind kind, DateTimeOffset stopTime)
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
        var (pcm, format) = _audio.StopCaptureAndGetPcm();

        var metadata = new RecordingMetadata(
            ActivationPressTime: _activationPressTime,
            RecordingStart: _recordingStartTime,
            MicCaptureStarted: _micCaptureStartedTime,
            StopActivationTime: _stopActivationTime,
            CaptureStopped: stopTime,
            WavFinalized: DateTimeOffset.UtcNow,
            TotalDurationSeconds: (stopTime - _recordingStartTime).TotalSeconds);

        if (_debugWriter is not null && _config.DebugOutputEnabled && pcm.Length > 0)
        {
            try
            {
                _debugWriter.Save(pcm, format, metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save debug recording");
            }
        }

        var target = kind == InteractionKind.Dictation ? _dictationTarget : default;
        _dictationTarget = default;
        return pcm.Length == 0
            ? null
            : new PipelineInput(kind, target, pcm, format, metadata, DateTimeOffset.UtcNow);
    }

    private async Task RunPipelineAsync(PipelineInput input)
    {
        var pipelineStart = input.PipelineStart;
        TranscriptionResult? transcription = null;
        DecisionResult? decision = null;
        ProgramResult? programResult = null;
        TextInsertionResult? insertionResult = null;
        DateTimeOffset? sttStart = null, sttEnd = null, jevStart = null, jevEnd = null;

        try
        {
            PublishState(input.Kind, ActivationState.Transcribing);
            if (_speechRecognizer is not null)
            {
                sttStart = DateTimeOffset.UtcNow;
                try
                {
                    var samples = PcmToFloatConverter.Convert16BitPcmToFloat(input.Pcm);
                    transcription = await _speechRecognizer.TranscribeAsync(samples.AsMemory()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "STT error");
                }
                sttEnd = DateTimeOffset.UtcNow;
            }

            if (transcription is null || !transcription.Success || string.IsNullOrWhiteSpace(transcription.Transcript))
            {
                var reason = transcription?.Error ?? (_speechRecognizer is null ? "No STT service" : "Empty transcript");
                _logger.LogInformation("Skipping {Kind} pipeline — {Reason}", input.Kind, reason);
                PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind, Status: reason));
                return;
            }

            PublishSnapshot(new(ApplicationInteractionPhase.Routing, input.Kind,
                transcription.Transcript, "Routing input"));

            if (input.Kind == InteractionKind.Dictation)
            {
                if (_textInsertion is null)
                {
                    PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind,
                        transcription.Transcript, "Text insertion is unavailable"));
                    return;
                }

                PublishSnapshot(new(ApplicationInteractionPhase.Executing, input.Kind,
                    transcription.Transcript, "Inserting dictation"));
                insertionResult = await _textInsertion.InsertAsync(
                    new TextInsertionRequest(transcription.Transcript, input.DictationTarget)).ConfigureAwait(false);
                PublishSnapshot(new(
                    insertionResult.Succeeded ? ApplicationInteractionPhase.Succeeded : ApplicationInteractionPhase.Failed,
                    input.Kind,
                    transcription.Transcript,
                    insertionResult.Detail ?? insertionResult.Status.ToString()));
                return;
            }

            PublishSnapshot(new(ApplicationInteractionPhase.Observing, input.Kind,
                transcription.Transcript, "Collecting direct capabilities"));
            DecisionState? decisionState = null;
            if (_decisionEngine is not null)
            {
                var windowsSw = Stopwatch.StartNew();
                var windows = CandidateBuilder.GetOpenWindowsWithTimings(out var winTimings);
                windowsSw.Stop();
                var appsSw = Stopwatch.StartNew();
                var apps = _catalog is not null
                    ? CandidateBuilder.GetInstalledApps(_catalog)
                    : (IReadOnlyList<AppCandidate>)[];
                appsSw.Stop();
                var topoSw = Stopwatch.StartNew();
                var topology = _topoService?.CaptureTopology() ?? DisplayTopology.Empty;
                topoSw.Stop();
                var foreground = CandidateBuilder.GetForegroundAppName();
                decisionState = new DecisionState(
                    transcription.Transcript, foreground, apps, windows,
                    [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
                    [SnapDirection.Left, SnapDirection.Right], topology);

                _logger.LogInformation(
                    "Decision prep: windows={WindowsMs:F0}ms (enum={EnumMs:F0}ms proc={ProcMs:F0}ms aumid={AumidMs:F0}ms hit={CacheHits} miss={CacheMisses}) apps={AppsMs:F0}ms topo={TopoMs:F0}ms",
                    windowsSw.ElapsedMilliseconds, winTimings.EnumerateMs, winTimings.ProcessMs,
                    winTimings.AumidMs, winTimings.CacheHits, winTimings.CacheMisses,
                    appsSw.ElapsedMilliseconds, topoSw.ElapsedMilliseconds);

                PublishState(input.Kind, ActivationState.Understanding);
                PublishSnapshot(new(ApplicationInteractionPhase.Deciding, input.Kind,
                    transcription.Transcript, "Choosing a bounded direct action"));
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

            if (_programExecutor is not null && decision is not null && decisionState is not null)
            {
                var compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var compound)
                    && compound.QuestionType == "noul"
                    && compound.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;
                var program = decision.Program
                    ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));

                if (program is not null)
                {
                    PublishSnapshot(new(ApplicationInteractionPhase.Executing, input.Kind,
                        transcription.Transcript, "Executing direct capability"));
                    try
                    {
                        programResult = await _programExecutor
                            .ExecuteAsync(program, decisionState.OpenWindows, decisionState.Topology)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Execution error for program ({Steps} steps)", program.Steps.Count);
                    }
                }
            }

            PublishSnapshot(new(
                programResult?.AllSucceeded == true
                    ? ApplicationInteractionPhase.Succeeded
                    : ApplicationInteractionPhase.Failed,
                input.Kind,
                transcription.Transcript,
                programResult?.AllSucceeded == true ? "Completed" : "No direct action completed"));
        }
        finally
        {
            var result = new PipelineResult(
                input.Metadata, transcription, decision, decision?.Plan,
                pipelineStart, sttStart, sttEnd, jevStart, jevEnd, DateTimeOffset.UtcNow,
                programResult, input.Kind, insertionResult);
            LogPipelineSummary(result);
            PublishState(input.Kind, ActivationState.Idle);
        }
    }

    private void LogPipelineSummary(PipelineResult result)
    {
        var sttMs = result.SttStart.HasValue && result.SttEnd.HasValue
            ? (result.SttEnd.Value - result.SttStart.Value).TotalMilliseconds : 0;
        var jevMs = result.JevStart.HasValue && result.JevEnd.HasValue
            ? (result.JevEnd.Value - result.JevStart.Value).TotalMilliseconds : 0;
        var execution = result.ProgramExecution is null ? "-"
            : result.ProgramExecution.AllSucceeded
                ? $"ok({result.ProgramExecution.ExecutedCount})"
                : $"fail({result.ProgramExecution.FirstFailure?.Status})";

        _logger.LogInformation(
            "Pipeline: kind={Kind} speech={SpeechMs:F0}ms STT={SttMs:F0}ms Jev={JevMs:F0}ms total={TotalMs:F0}ms chars={Characters} action={Action} confidence={Confidence:P0} exec={ExecStatus} insertion={InsertionStatus}",
            result.Kind, result.Recording.TotalDurationSeconds * 1000, sttMs, jevMs,
            (result.PipelineEnd - result.PipelineStart).TotalMilliseconds,
            result.Transcription?.Transcript.Length ?? 0,
            result.Plan?.Action.ToString() ?? "none", result.Plan?.Confidence ?? 0,
            execution, result.TextInsertion?.Status.ToString() ?? "-");
    }

    private MachineContext GetContext(InteractionKind kind)
        => kind == InteractionKind.Command ? _commandContext : _dictationContext;

    private void SetContext(InteractionKind kind, MachineContext context)
    {
        if (kind == InteractionKind.Command)
            _commandContext = context;
        else
            _dictationContext = context;
    }

    private ActivationMode GetMode(InteractionKind kind)
        => kind == InteractionKind.Command ? _config.ActivationMode : _config.DictationActivationMode;

    private void PublishState(InteractionKind kind, ActivationState state)
    {
        StateChanged?.Invoke(this, state);
        InteractionChanged?.Invoke(this, new(kind, state));
        var snapshot = state switch
        {
            ActivationState.Recording => new ApplicationInteractionSnapshot(
                ApplicationInteractionPhase.Listening, kind, Status: "Listening"),
            ActivationState.Stopping => new ApplicationInteractionSnapshot(
                ApplicationInteractionPhase.Listening, kind, Status: "Finishing capture"),
            ActivationState.Transcribing => new ApplicationInteractionSnapshot(
                ApplicationInteractionPhase.Transcribing, kind, Status: "Transcribing"),
            ActivationState.Understanding => new ApplicationInteractionSnapshot(
                ApplicationInteractionPhase.Deciding, kind, Status: "Deciding"),
            _ => ApplicationInteractionSnapshot.Idle
        };
        PublishSnapshot(snapshot);
    }

    private void PublishSnapshot(ApplicationInteractionSnapshot snapshot)
        => InteractionSnapshotChanged?.Invoke(this, new(snapshot));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commandHook.KeyDown -= OnCommandKeyDown;
        _commandHook.KeyUp -= OnCommandKeyUp;
        _dictationHook.KeyDown -= OnDictationKeyDown;
        _dictationHook.KeyUp -= OnDictationKeyUp;
        _graceTimer?.Dispose();
        _commandHook.Dispose();
        _dictationHook.Dispose();
        _audio.Dispose();
        _speechRecognizer?.Dispose();
    }

    private sealed record PipelineInput(
        InteractionKind Kind,
        TextInsertionTarget DictationTarget,
        byte[] Pcm,
        WaveFormat Format,
        RecordingMetadata Metadata,
        DateTimeOffset PipelineStart);
}
