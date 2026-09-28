using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Browser;
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
    private readonly ICommandRouter? _commandRouter;
    private readonly IBrowserInteractionService? _browserInteraction;
    private readonly IChromeCompanionTransport? _browserTransport;
    private readonly IWindowAwareLauncher? _windowAwareLauncher;
    private readonly ScopeResolver _scopeResolver = new();
    private RecentTaskFrame? _recentTask;
    internal RecentTaskFrame? RecentTaskForTesting => _recentTask;
    private readonly VoiceOSConfig _config;
    private readonly ILogger<ActivationOrchestrator> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly object _stateLock = new();
    private MachineContext _commandContext = MachineContext.Initial;
    private MachineContext _dictationContext = MachineContext.Initial;
    private InteractionKind? _activeCapture;
    private TextInsertionTarget _dictationTarget;
    private System.Threading.Timer? _graceTimer;
    private bool _disposed;
    private long _uiGeneration;
    private readonly AsyncLocal<long?> _uiRun = new();
    private readonly AsyncLocal<ActivationRun?> _activeRun = new();

    private DateTimeOffset _activationPressTime;
    private DateTimeOffset _recordingStartTime;
    private DateTimeOffset _micCaptureStartedTime;
    private DateTimeOffset _stopActivationTime;

    public event EventHandler<ActivationState>? StateChanged;
    public event EventHandler<InteractionStateChanged>? InteractionChanged;
    public event EventHandler<ApplicationInteractionSnapshotEventArgs>? InteractionSnapshotChanged;
    public event EventHandler<ProductUiLifecycle>? ProductUiChanged;

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
        ITextInsertionService? textInsertion = null,
        ICommandRouter? commandRouter = null,
        IBrowserInteractionService? browserInteraction = null,
        IChromeCompanionTransport? browserTransport = null,
        IWindowAwareLauncher? windowAwareLauncher = null)
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
        _commandRouter = commandRouter;
        _browserInteraction = browserInteraction;
        if (_browserInteraction is IBrowserActivitySource activity)
            activity.ActionStarting += OnBrowserActionStarting;
        _browserTransport = browserTransport;
        _windowAwareLauncher = windowAwareLauncher;

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

        if (pipeline is not null) PublishUi(ProductUiPhase.Understanding);
        PublishState(kind, newState, updateUi: pipeline is null);
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

        if (pipeline is not null) PublishUi(ProductUiPhase.Understanding);
        PublishState(kind, newState, updateUi: pipeline is null);
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
                        Interlocked.Increment(ref _uiGeneration);
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
            : new PipelineInput(kind, target, pcm, format, metadata, DateTimeOffset.UtcNow,
                Volatile.Read(ref _uiGeneration));
    }

    private async Task RunPipelineAsync(PipelineInput input)
    {
        _uiRun.Value = input.UiGeneration;
        var run = new ActivationRun(Guid.NewGuid().ToString("N"), input.Kind, ActivationSource.Voice);
        _activeRun.Value = run;
        using var activationScope = _logger.BeginScope("activation_id={ActivationId}", run.ActivationId);
        var pipelineStart = input.PipelineStart;
        TranscriptionResult? transcription = null;
        TextInsertionResult? insertionResult = null;
        DateTimeOffset? sttStart = null, sttEnd = null;

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

            if (transcription is { Success: true } && string.IsNullOrWhiteSpace(transcription.Transcript))
            {
                run.Outcome = "NoSpeech";
                PublishUi(ProductUiPhase.Idle);
                PublishSnapshot(new(ApplicationInteractionPhase.Idle, input.Kind,
                    Status: "No usable speech"));
                return;
            }

            if (transcription is null || !transcription.Success)
            {
                var reason = transcription?.Error ?? (_speechRecognizer is null ? "No STT service" : "Empty transcript");
                _logger.LogInformation("Skipping {Kind} pipeline — {Reason}", input.Kind, reason);
                PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind, Status: reason));
                return;
            }

            run.Transcript = transcription.Transcript;
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
                var insertionTimer = Stopwatch.StartNew();
                insertionResult = await _textInsertion.InsertAsync(
                    new TextInsertionRequest(transcription.Transcript, input.DictationTarget)).ConfigureAwait(false);
                insertionTimer.Stop();
                run.ActionCount = 1;
                run.Outcome = insertionResult.Succeeded ? "Succeeded" : "Failed";
                _logger.LogInformation("Dictation insertion outcome={Outcome} latency_ms={LatencyMs:F0}",
                    insertionResult.Status, insertionTimer.Elapsed.TotalMilliseconds);
                PublishSnapshot(new(
                    insertionResult.Succeeded ? ApplicationInteractionPhase.Succeeded : ApplicationInteractionPhase.Failed,
                    input.Kind,
                    transcription.Transcript,
                    insertionResult.Detail ?? insertionResult.Status.ToString()));
                return;
            }

            run.PostSttStart = sttEnd ?? DateTimeOffset.UtcNow;
            _logger.LogInformation("Activation transcript activation_id={ActivationId} mode={Mode} exact_transcript={Transcript} speech_ms={SpeechMs:F0} stt_ms={SttMs:F0}",
                run.ActivationId, input.Kind, transcription.Transcript,
                input.Metadata.TotalDurationSeconds * 1000,
                sttStart.HasValue && sttEnd.HasValue ? (sttEnd.Value - sttStart.Value).TotalMilliseconds : 0);
            await ExecuteCommandTranscriptAsync(run, transcription.Transcript).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            PublishUi(ProductUiPhase.Idle);
        }
        catch (Exception ex)
        {
            run.Failure = ex;
            _logger.LogError(ex, "Activation pipeline failed");
            PublishUi(ProductUiPhase.Error, "Couldn't complete that action.");
        }
        finally
        {
            var result = new PipelineResult(
                input.Metadata, transcription, run.Decision, run.Decision?.Plan,
                pipelineStart, sttStart, sttEnd, run.JevStart, run.JevEnd, DateTimeOffset.UtcNow,
                run.ProgramResult, input.Kind, insertionResult);
            LogPipelineSummary(result, run.ActivationId, run.Lane, run.Outcome, run.PostSttStart, run.ActionCount);
            CompleteActivation(run);
        }
    }

    /// <summary>
    /// Post-STT entry point: runs the command lifecycle for an already recognized transcript,
    /// exactly as a voice activation does after successful transcription (routing, context,
    /// scope, direct/browser execution, recent-task continuity, UI lifecycle). Used by the live
    /// eval harness; microphone capture and STT are the only stages it skips.
    /// </summary>
    public async Task<ActivationRun> RunTranscriptAsync(string transcript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        _uiRun.Value = Interlocked.Increment(ref _uiGeneration);
        var run = new ActivationRun(Guid.NewGuid().ToString("N"), InteractionKind.Command,
            ActivationSource.InjectedTranscript) { Transcript = transcript };
        _activeRun.Value = run;
        using var activationScope = _logger.BeginScope("activation_id={ActivationId}", run.ActivationId);
        try
        {
            PublishUi(ProductUiPhase.Understanding);
            PublishSnapshot(new(ApplicationInteractionPhase.Routing, InteractionKind.Command,
                transcript, "Routing input"));
            run.PostSttStart = DateTimeOffset.UtcNow;
            _logger.LogInformation("Activation transcript activation_id={ActivationId} mode={Mode} exact_transcript={Transcript} source={Source}",
                run.ActivationId, InteractionKind.Command, transcript, run.Source);
            await ExecuteCommandTranscriptAsync(run, transcript).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            PublishUi(ProductUiPhase.Idle);
        }
        catch (Exception ex)
        {
            run.Failure = ex;
            _logger.LogError(ex, "Activation pipeline failed");
            PublishUi(ProductUiPhase.Error, "Couldn't complete that action.");
        }
        finally
        {
            _logger.LogInformation("Activation final activation_id={ActivationId} mode={Kind} source={Source} lane={Lane} outcome={Outcome} post_stt_ms={PostSttMs:F0} actions={Actions}",
                run.ActivationId, run.Kind, run.Source, run.Lane, run.Outcome,
                run.PostSttStart is { } start ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : 0,
                run.ActionCount);
            CompleteActivation(run);
        }
        return run;
    }

    private void CompleteActivation(ActivationRun run)
    {
        run.CompletedAt = DateTimeOffset.UtcNow;
        if (run.Trace is { } trace)
            _logger.LogInformation("Activation timings activation_id={ActivationId} lane={Lane} outcome={Outcome} {Timings}",
                run.ActivationId, run.Lane, run.Outcome, trace.Format());
        PublishState(run.Kind, ActivationState.Idle, updateUi: false);
    }

    /// <summary>The shared command lifecycle after a transcript is available.</summary>
    private async Task ExecuteCommandTranscriptAsync(ActivationRun run, string transcript)
    {
        var activationId = run.ActivationId;
        var kind = run.Kind;
        var trace = run.Trace = LatencyTrace.Begin(activationId);

        PublishState(kind, ActivationState.Understanding);
        var collected = await ExecutionContextCollector.CollectAsync(transcript, _catalog, _topoService,
            _browserTransport, _recentTask, trace, _logger, _shutdown.Token).ConfigureAwait(false);
        _recentTask = collected.StoredRecentTask;
        var executionContext = collected.Snapshot;
        var decisionState = collected.DecisionState;
        var exposedRecentTask = executionContext.RecentTask;
        CommandRouteDecision route;
        var routeTimer = Stopwatch.StartNew();
        try
        {
            route = _commandRouter switch
            {
                null => new CommandRouteDecision(CommandRoute.DirectCapability, 1, "No command router is configured."),
                TypeSafeCommandRouter semantic => await semantic.RouteAsync(
                    transcript, exposedRecentTask, _shutdown.Token).ConfigureAwait(false),
                _ => await _commandRouter.RouteAsync(transcript, _shutdown.Token).ConfigureAwait(false)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            _logger.LogError(ex, "Command routing failed");
            PublishUnavailable(run, UnavailableReason.IntentService, ActivityMessage.ForUnavailable(UnavailableReason.IntentService));
            return;
        }
        routeTimer.Stop();
        trace.Record("route", routeTimer.Elapsed.TotalMilliseconds);
        run.InitialRoute = run.Route = route;
        _logger.LogInformation("Command route heads destination_kind={DestinationKind} destination={Destination} tab_disposition={TabDisposition} context_dependency={ContextDependency} task_relation={TaskRelation} surface_preference={SurfacePreference} goal_shape={GoalShape} end_state={EndState}",
            route.DestinationKind, route.DestinationName ?? "-", route.TabDisposition, route.ContextDependency,
            route.TaskRelation, route.SurfacePreference, route.GoalShape, route.EndState);
        run.Lane = route.Route.ToString();
        _logger.LogInformation("Command route={Route} confidence={Confidence:P0} reason={Reason} media_request_kind={MediaRequestKind} media_op={MediaOp} route_ms={RouteMs:F0}",
            route.Route, route.Confidence, route.Reason, route.MediaRequestKind,
            route.MediaOperation?.ToString() ?? "-", routeTimer.Elapsed.TotalMilliseconds);
        if (route.Route == CommandRoute.Clarify)
            _logger.LogInformation("Clarification level=routing category={Category} confidence={Confidence:P0} reason={Reason}",
                route.Reason, route.Confidence, route.Detail);

        var reroutedFromDirect = false;
    resolveScope:
        // A named entity without a registered destination still needs normalization to be
        // discovered, even when reaching it is the whole goal.
        if (route.Route == CommandRoute.ComputerUse && route.SurfacePreference != SurfacePreference.Native
            && (route.GoalShape != GoalShape.SurfaceOnly
                || route.RequestsNamedEntity && route.DestinationKind == SemanticDestinationKind.None))
            _browserInteraction?.PrefetchNormalization(transcript, activationId, _shutdown.Token);

        executionContext = collected.Snapshot with
        {
            RecentTask = RecentTaskPolicy.ExposedForScope(exposedRecentTask, route)
        };
        run.ExecutionContext = executionContext;
        var scopeTimer = Stopwatch.StartNew();
        ExecutionScopeDecision executionScope;
        try
        {
            executionScope = await _scopeResolver.ResolveAsync(transcript, route,
                executionContext, _commandRouter as IContextualScopeDecisionSource,
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            _logger.LogError(ex, "Scope selection service failed");
            PublishUnavailable(run, UnavailableReason.IntentService, ActivityMessage.ForUnavailable(UnavailableReason.IntentService));
            return;
        }
        scopeTimer.Stop();
        trace.Record("scope", scopeTimer.Elapsed.TotalMilliseconds);
        run.Scope = executionScope;
        _logger.LogInformation("Execution scope={Scope} browser_scope={BrowserScope} tab={TabId} detail={Detail}",
            executionScope.Kind, executionScope.Browser?.Kind, executionScope.Browser?.TabId,
            executionScope.Detail);

        if (executionScope.Kind == ExecutionScopeKind.Browser)
        {
            if (_browserInteraction is null)
            {
                PublishSnapshot(new(ApplicationInteractionPhase.Failed, kind,
                    transcript, "Managed browser interaction is unavailable"));
                return;
            }

            if (_browserTransport?.IsConnected != true
                && !await StartChromeCompanionAsync(_shutdown.Token).ConfigureAwait(false))
            {
                PublishUnavailable(run, UnavailableReason.ChromeCompanion,
                    "Chrome did not connect to the Companion within the startup period.");
                return;
            }

            PublishSnapshot(new(ApplicationInteractionPhase.Observing, kind,
                transcript, "Framing browser goal and acquiring managed context"));
            var browserResult = await _browserInteraction.RunAsync(transcript, _shutdown.Token,
                activationId, executionScope.Browser).ConfigureAwait(false);
            run.BrowserOutcome = browserResult;
            if (browserResult.Unavailable is { } unavailable)
            {
                run.ActionCount = browserResult.Actions;
                PublishUnavailable(run, unavailable, browserResult.Detail);
                return;
            }
            _recentTask = RecentTaskPolicy.AfterBrowserRun(_recentTask, browserResult, transcript, DateTimeOffset.UtcNow);
            run.ActionCount = browserResult.Actions;
            run.Outcome = browserResult.Completion.ToString();
            if (browserResult.Completion == InteractionCompletionState.Uncertain)
                _logger.LogInformation("Clarification level=task_local category=browser_uncertain reason={Reason} choices={ChoiceCount}",
                    browserResult.Detail, browserResult.Choices?.Count ?? 0);
            var browserPhase = browserResult.Completion switch
            {
                InteractionCompletionState.Complete => ApplicationInteractionPhase.Succeeded,
                InteractionCompletionState.Uncertain => ApplicationInteractionPhase.NeedsChoice,
                _ => ApplicationInteractionPhase.Failed
            };
            PublishSnapshot(new(browserPhase, kind, transcript,
                browserResult.Detail, browserResult.Choices, browserResult.Completion));
            return;
        }

        if (executionScope.Kind == ExecutionScopeKind.NativeInteraction)
        {
            if (executionScope.Native is { AppCandidateId: { } appId,
                    EndState: SemanticEndState.SurfaceReady }
                && route.GoalShape == GoalShape.SurfaceOnly
                && _windowAwareLauncher is not null)
            {
                PublishUi(ProductUiPhase.Acting, ActivityMessage.ForNativeApp(appId, _catalog));
                trace.MarkExternalAction();
                var prepared = await _windowAwareLauncher.FocusOrLaunchAsync(
                    new AppTarget(appId), executionContext.OpenWindows, _shutdown.Token)
                    .ConfigureAwait(false);
                run.Outcome = prepared.Succeeded ? "Succeeded" : "Failed";
                PublishSnapshot(new(prepared.Succeeded ? ApplicationInteractionPhase.Succeeded
                    : ApplicationInteractionPhase.Failed, kind, transcript,
                    prepared.Detail));
                return;
            }
            run.Outcome = "Unsupported";
            PublishSnapshot(new(ApplicationInteractionPhase.Failed, kind,
                transcript, "Native UI interaction is not enabled yet."));
            return;
        }

        if (executionScope.Kind is ExecutionScopeKind.TextTransform or ExecutionScopeKind.Clarify)
        {
            run.Outcome = "Clarify";
            var status = executionScope.Kind == ExecutionScopeKind.TextTransform
                ? "Text transformation is not part of the current literal dictation or browser stage."
                : executionScope.Detail ?? route.Detail ?? "The command needs clarification.";
            PublishSnapshot(new(ApplicationInteractionPhase.NeedsChoice, kind,
                transcript, status,
                [new InteractionChoice("retry", "Clarify request"), new InteractionChoice("cancel", "Cancel")],
                InteractionCompletionState.Uncertain));
            return;
        }

        PublishSnapshot(new(ApplicationInteractionPhase.Observing, kind,
            transcript, "Collecting direct capabilities"));
        if (_decisionEngine is not null || route.MediaOperation is not null)
        {
            if (route.MediaOperation is { } mediaOperation)
            {
                var plan = new VoicePlan(VoiceAction.MediaControl, Media: mediaOperation, Confidence: 1);
                run.Decision = new DecisionResult(plan, SemanticProgramPlanner.ToSingleStepProgram(plan),
                    new Dictionary<string, JevAnswer>(), 0, 0, 0);
            }
            else if (_decisionEngine is not null)
            {
                PublishState(kind, ActivationState.Understanding);
                PublishSnapshot(new(ApplicationInteractionPhase.Deciding, kind,
                    transcript, "Choosing a bounded direct action"));
                run.JevStart = DateTimeOffset.UtcNow;
                try
                {
                    run.Decision = await _decisionEngine.DecideAsync(decisionState).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Decision engine error");
                    PublishUnavailable(run, UnavailableReason.IntentService, ActivityMessage.ForUnavailable(UnavailableReason.IntentService));
                    return;
                }
                run.JevEnd = DateTimeOffset.UtcNow;
                trace.Record("direct_decision", (run.JevEnd.Value - run.JevStart.Value).TotalMilliseconds);
            }
            if (run.Decision?.ProviderFailed == true)
            {
                PublishUnavailable(run, UnavailableReason.IntentService, ActivityMessage.ForUnavailable(UnavailableReason.IntentService));
                return;
            }
            if (run.Decision is { } made)
                _logger.LogInformation("Direct decision action_kind={ActionKind} media_op={MediaOp} snap_dir={SnapDir} target_app={TargetApp} target_window={TargetWindow} jev_ms={JevMs:F0}",
                    made.Plan.Action,
                    made.Plan.Action == VoiceAction.MediaControl ? made.Plan.Media?.ToString() : null,
                    made.Plan.Action == VoiceAction.SnapCurrentWindow ? made.Plan.Snap?.ToString() : null,
                    made.Plan.AppCandidateId,
                    made.Plan.WindowCandidateId,
                    run.JevStart.HasValue && run.JevEnd.HasValue ? (run.JevEnd.Value - run.JevStart.Value).TotalMilliseconds : 0);
        }

        if (_programExecutor is not null && run.Decision is { } decision && decisionState is not null)
        {
            var compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var compound)
                && compound.QuestionType == "noul"
                && compound.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;
            var program = decision.Program
                ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));
            run.Program = program;

            var targetVerdict = reroutedFromDirect ? DirectTargetVerdict.Proceed
                : DirectTargetPolicy.Evaluate(route, decision.Plan, program, _catalog);
            run.DirectTargetVerdict = targetVerdict;
            if (targetVerdict != DirectTargetVerdict.Proceed)
                _logger.LogInformation("Direct target rejected verdict={Verdict} requested_entity={Entity} destination={Destination} target_app={TargetApp}",
                    targetVerdict, route.RequestedEntity, route.DestinationName ?? "-", decision.Plan.AppCandidateId ?? "-");
            if (targetVerdict is DirectTargetVerdict.NativeUnavailable or DirectTargetVerdict.NativeMismatch)
            {
                run.Outcome = "Clarify";
                PublishSnapshot(new(ApplicationInteractionPhase.NeedsChoice, kind,
                    transcript, targetVerdict == DirectTargetVerdict.NativeUnavailable
                        ? "The requested app isn't installed."
                        : "The chosen app doesn't match the requested app.",
                    [new InteractionChoice("retry", "Clarify request"), new InteractionChoice("cancel", "Cancel")],
                    InteractionCompletionState.Uncertain));
                return;
            }
            if (targetVerdict == DirectTargetVerdict.RerouteToBrowser)
            {
                // No installed app represents the requested entity, or the request addresses a
                // browser tab; continue to its web representation, discovery, or tab surface.
                reroutedFromDirect = true;
                route = route with { Route = CommandRoute.ComputerUse, Detail = route.TabDisposition != TabDisposition.Unspecified
                    ? "The request addresses a browser tab." : "No installed app represents the requested entity." };
                run.Route = route;
                run.ReroutedFromDirect = true;
                run.Lane = route.Route.ToString();
                run.Decision = null;
                run.Program = null;
                goto resolveScope;
            }

            if (_commandRouter is not null
                && program?.Steps.Any(static step => step is MediaControlStep) == true
                && route.MediaRequestKind != MediaRequestKind.Transport)
            {
                _logger.LogWarning("Direct media execution blocked: media_request_kind={MediaRequestKind}",
                    route.MediaRequestKind);
                run.Outcome = "Clarify";
                PublishSnapshot(new(ApplicationInteractionPhase.NeedsChoice, kind,
                    transcript, "Media intent needs clarification before controlling current playback.",
                    [new InteractionChoice("retry", "Clarify request"), new InteractionChoice("cancel", "Cancel")],
                    InteractionCompletionState.Uncertain));
                return;
            }

            if (program is not null)
            {
                _logger.LogInformation("Direct executor actions={Actions}",
                    string.Join(",", program.Steps.Select(static step => step.GetType().Name)));
                PublishSnapshot(new(ApplicationInteractionPhase.Executing, kind,
                    transcript, "Executing direct capability"));
                var executionTimer = Stopwatch.StartNew();
                try
                {
                    run.ProgramResult = await _programExecutor
                        .ExecuteAsync(program, decisionState.OpenWindows, decisionState.Topology,
                            onStepStarting: step =>
                            {
                                trace?.MarkExternalAction();
                                PublishUi(ProductUiPhase.Acting,
                                    ActivityMessage.ForStep(step, _catalog, decisionState.OpenWindows));
                            })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Execution error for program ({Steps} steps)", program.Steps.Count);
                }
                executionTimer.Stop();
                trace.Record("direct_execution", executionTimer.Elapsed.TotalMilliseconds);
                run.ActionCount = run.ProgramResult?.ExecutedCount ?? 0;
                _logger.LogInformation("Direct executor outcome={Outcome} executed={Executed} latency_ms={LatencyMs:F0}",
                    run.ProgramResult?.AllSucceeded == true ? "Succeeded" : "Failed",
                    run.ActionCount, executionTimer.Elapsed.TotalMilliseconds);
            }
        }

        run.Outcome = run.ProgramResult?.AllSucceeded == true ? "Succeeded" : "Failed";
        PublishSnapshot(new(
            run.ProgramResult?.AllSucceeded == true
                ? ApplicationInteractionPhase.Succeeded
                : ApplicationInteractionPhase.Failed,
            kind,
            transcript,
            run.ProgramResult?.AllSucceeded == true ? "Completed" : "No direct action completed"));
    }

    private async Task<bool> StartChromeCompanionAsync(CancellationToken cancellationToken)
    {
        if (_browserTransport?.IsConnected == true) return true;
        var chrome = _catalog?.GetAll().Where(entry =>
            string.Equals(entry.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (chrome is not { Length: 1 }) return false;
        try
        {
            var running = Process.GetProcessesByName("chrome");
            try
            {
                if (running.Length == 0)
                {
                    var entry = chrome[0];
                    var start = entry.LaunchKind == AppLaunchKind.PackagedApp
                        ? new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.LaunchTarget}") { UseShellExecute = true }
                        : new ProcessStartInfo(entry.LaunchTarget) { UseShellExecute = true,
                            Arguments = entry.LaunchArguments ?? "" };
                    Process.Start(start);
                }
            }
            finally { foreach (var process in running) process.Dispose(); }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(12));
            while (!bounded.IsCancellationRequested)
            {
                if (_browserTransport?.IsConnected == true) return true;
                await Task.Delay(200, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Chrome startup failed"); }
        return _browserTransport?.IsConnected == true;
    }

    private void LogPipelineSummary(PipelineResult result, string activationId, string lane,
        string outcome, DateTimeOffset? postSttStart, int actionCount)
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
            "Activation final activation_id={ActivationId} mode={Kind} lane={Lane} outcome={Outcome} post_stt_ms={PostSttMs:F0} total_ms={TotalMs:F0} speech_ms={SpeechMs:F0} stt_ms={SttMs:F0} jev_ms={JevMs:F0} action={Action} actions={Actions} confidence={Confidence:P0} exec={ExecStatus} insertion={InsertionStatus}",
            activationId, result.Kind, lane, outcome,
            postSttStart.HasValue ? (result.PipelineEnd - postSttStart.Value).TotalMilliseconds : 0,
            (result.PipelineEnd - result.PipelineStart).TotalMilliseconds,
            result.Recording.TotalDurationSeconds * 1000, sttMs, jevMs,
            result.Plan?.Action.ToString() ?? "none", actionCount, result.Plan?.Confidence ?? 0,
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

    private void PublishState(InteractionKind kind, ActivationState state, bool updateUi = true)
    {
        if (updateUi)
            PublishUi(state switch
            {
                ActivationState.Recording => ProductUiPhase.Listening,
                ActivationState.Stopping or ActivationState.Transcribing or ActivationState.Understanding
                    => ProductUiPhase.Understanding,
                _ => ProductUiPhase.Idle
            });
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

    private void PublishUnavailable(ActivationRun run, UnavailableReason reason, string? detail)
    {
        run.Outcome = "Unavailable";
        _logger.LogWarning("Outcome unavailable reason={Reason} detail={Detail}", reason, detail);
        PublishSnapshot(new(ApplicationInteractionPhase.Unavailable, run.Kind, run.Transcript, detail, Unavailable: reason));
    }

    private void PublishSnapshot(ApplicationInteractionSnapshot snapshot)
    {
        switch (snapshot.Phase)
        {
            case ApplicationInteractionPhase.Unavailable:
                PublishUi(ProductUiPhase.Error, ActivityMessage.ForUnavailable(snapshot.Unavailable));
                break;
            case ApplicationInteractionPhase.Succeeded:
                PublishUi(ProductUiPhase.Success);
                break;
            case ApplicationInteractionPhase.NeedsChoice:
                PublishUi(ProductUiPhase.Clarify, ActivityMessage.ForClarification(snapshot.Status));
                break;
            case ApplicationInteractionPhase.Failed:
                PublishUi(ProductUiPhase.Error, ActivityMessage.ForFailure(snapshot.Status));
                break;
        }
        _activeRun.Value?.AddSnapshot(snapshot);
        InteractionSnapshotChanged?.Invoke(this, new(snapshot));
    }

    private void PublishUi(ProductUiPhase phase, string? message = null)
    {
        if (_uiRun.Value is { } run && run != Volatile.Read(ref _uiGeneration)) return;
        ProductUiChanged?.Invoke(this, new(phase, message, _uiRun.Value ?? Volatile.Read(ref _uiGeneration)));
    }

    private void OnBrowserActionStarting(BrowserActivity action)
    {
        LatencyTrace.Current?.MarkExternalAction();
        _activeRun.Value?.AddBrowserActivity(action);
        PublishUi(ProductUiPhase.Acting, ActivityMessage.ForBrowserAction(action));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _commandHook.KeyDown -= OnCommandKeyDown;
        _commandHook.KeyUp -= OnCommandKeyUp;
        _dictationHook.KeyDown -= OnDictationKeyDown;
        _dictationHook.KeyUp -= OnDictationKeyUp;
        if (_browserInteraction is IBrowserActivitySource activity)
            activity.ActionStarting -= OnBrowserActionStarting;
        _graceTimer?.Dispose();
        _commandHook.Dispose();
        _dictationHook.Dispose();
        _audio.Dispose();
        _speechRecognizer?.Dispose();
        if (_browserInteraction is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _shutdown.Dispose();
    }

    private sealed record PipelineInput(
        InteractionKind Kind,
        TextInsertionTarget DictationTarget,
        byte[] Pcm,
        WaveFormat Format,
        RecordingMetadata Metadata,
        DateTimeOffset PipelineStart,
        long UiGeneration);
}
