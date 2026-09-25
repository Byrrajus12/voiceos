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
        var activationId = Guid.NewGuid().ToString("N");
        using var activationScope = _logger.BeginScope("activation_id={ActivationId}", activationId);
        var pipelineStart = input.PipelineStart;
        DateTimeOffset? postSttStart = null;
        var lane = input.Kind == InteractionKind.Dictation ? "Dictation" : "Unrouted";
        var outcome = "Failed";
        var actionCount = 0;
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
                var insertionTimer = Stopwatch.StartNew();
                insertionResult = await _textInsertion.InsertAsync(
                    new TextInsertionRequest(transcription.Transcript, input.DictationTarget)).ConfigureAwait(false);
                insertionTimer.Stop();
                actionCount = 1;
                outcome = insertionResult.Succeeded ? "Succeeded" : "Failed";
                _logger.LogInformation("Dictation insertion outcome={Outcome} latency_ms={LatencyMs:F0}",
                    insertionResult.Status, insertionTimer.Elapsed.TotalMilliseconds);
                PublishSnapshot(new(
                    insertionResult.Succeeded ? ApplicationInteractionPhase.Succeeded : ApplicationInteractionPhase.Failed,
                    input.Kind,
                    transcription.Transcript,
                    insertionResult.Detail ?? insertionResult.Status.ToString()));
                return;
            }

            postSttStart = sttEnd ?? DateTimeOffset.UtcNow;
            _logger.LogInformation("Activation transcript activation_id={ActivationId} mode={Mode} exact_transcript={Transcript} speech_ms={SpeechMs:F0} stt_ms={SttMs:F0}",
                activationId, input.Kind, transcription.Transcript,
                input.Metadata.TotalDurationSeconds * 1000,
                sttStart.HasValue && sttEnd.HasValue ? (sttEnd.Value - sttStart.Value).TotalMilliseconds : 0);

            PublishState(input.Kind, ActivationState.Understanding);
            CommandRouteDecision route;
            var routeTimer = Stopwatch.StartNew();
            try
            {
                route = _commandRouter switch
                {
                    null => new CommandRouteDecision(CommandRoute.DirectCapability, 1, "No command router is configured."),
                    TypeSafeCommandRouter semantic => await semantic.RouteAsync(
                        transcription.Transcript, _recentTask, _shutdown.Token).ConfigureAwait(false),
                    _ => await _commandRouter.RouteAsync(transcription.Transcript, _shutdown.Token).ConfigureAwait(false)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Command routing failed");
                route = new(CommandRoute.Clarify, 0, "Command routing failed safely.", RoutingReason.RouterFailure);
            }
            routeTimer.Stop();
            if (route.TaskRelation == TaskRelation.NewTask)
                _recentTask = null;
            lane = route.Route.ToString();
            _logger.LogInformation("Command route={Route} confidence={Confidence:P0} reason={Reason} media_request_kind={MediaRequestKind} media_op={MediaOp} route_ms={RouteMs:F0}",
                route.Route, route.Confidence, route.Reason, route.MediaRequestKind,
                route.MediaOperation?.ToString() ?? "-", routeTimer.Elapsed.TotalMilliseconds);
            if (route.Route == CommandRoute.Clarify)
                _logger.LogInformation("Clarification level=routing category={Category} confidence={Confidence:P0} reason={Reason}",
                    route.Reason, route.Confidence, route.Detail);

            ExecutionContextSnapshot executionContext;
            if (route.Route is CommandRoute.ComputerUse or CommandRoute.NativeInteraction
                || route.Route == CommandRoute.Clarify && route.Reason != RoutingReason.IncompleteIntent
                    && route.Reason != RoutingReason.RouterFailure
                || route.MediaRequestKind == MediaRequestKind.ContentSelection)
            {
                var windows = CandidateBuilder.GetOpenWindows();
                IReadOnlyList<BrowserTabInfo> tabs = [];
                var connected = _browserTransport?.IsConnected == true;
                if (connected)
                {
                    try { tabs = await _browserTransport!.ListTabsAsync(_shutdown.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Browser scope inventory failed");
                        connected = false;
                    }
                }
                if (connected && _recentTask is { } prior
                    && !tabs.Any(tab => tab.TabId == prior.TabId
                        && tab.SessionId == prior.SessionId
                        && StringComparer.Ordinal.Equals(tab.Url, prior.Url)))
                    _recentTask = null;
                executionContext = new(windows.FirstOrDefault(static w => w.IsForeground),
                    windows, connected, tabs, _recentTask,
                    _catalog is null ? [] : CandidateBuilder.GetInstalledApps(_catalog));
            }
            else
                executionContext = new(null, [], false, []);
            var executionScope = await _scopeResolver.ResolveAsync(transcription.Transcript, route,
                executionContext, _commandRouter as IContextualScopeDecisionSource,
                _shutdown.Token).ConfigureAwait(false);
            _logger.LogInformation("Execution scope={Scope} browser_scope={BrowserScope} tab={TabId} detail={Detail}",
                executionScope.Kind, executionScope.Browser?.Kind, executionScope.Browser?.TabId,
                executionScope.Detail);

            if (executionScope.Kind == ExecutionScopeKind.Browser)
            {
                if (_browserInteraction is null)
                {
                    PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind,
                        transcription.Transcript, "Managed browser interaction is unavailable"));
                    return;
                }

                if (_browserTransport?.IsConnected != true
                    && !await StartChromeCompanionAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    outcome = "Unavailable";
                    PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind,
                        transcription.Transcript, "Chrome did not connect to the Companion within the startup period."));
                    return;
                }

                PublishSnapshot(new(ApplicationInteractionPhase.Observing, input.Kind,
                    transcription.Transcript, "Framing browser goal and acquiring managed context"));
                var browserResult = await _browserInteraction.RunAsync(transcription.Transcript, _shutdown.Token,
                    activationId, executionScope.Browser).ConfigureAwait(false);
                if (browserResult is { TabId: int tabId, SessionId: { } sessionId, Url: { } url }
                    && browserResult.Completion is InteractionCompletionState.Complete or InteractionCompletionState.Uncertain)
                    _recentTask = new(tabId, sessionId, url,
                        browserResult.SemanticGoal ?? transcription.Transcript,
                        browserResult.Completion, DateTimeOffset.UtcNow);
                actionCount = browserResult.Actions;
                outcome = browserResult.Completion.ToString();
                if (browserResult.Completion == InteractionCompletionState.Uncertain)
                    _logger.LogInformation("Clarification level=task_local category=browser_uncertain reason={Reason} choices={ChoiceCount}",
                        browserResult.Detail, browserResult.Choices?.Count ?? 0);
                var browserPhase = browserResult.Completion switch
                {
                    InteractionCompletionState.Complete => ApplicationInteractionPhase.Succeeded,
                    InteractionCompletionState.Uncertain => ApplicationInteractionPhase.NeedsChoice,
                    _ => ApplicationInteractionPhase.Failed
                };
                PublishSnapshot(new(browserPhase, input.Kind, transcription.Transcript,
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
                    var prepared = await _windowAwareLauncher.FocusOrLaunchAsync(
                        new AppTarget(appId), executionContext.OpenWindows, _shutdown.Token)
                        .ConfigureAwait(false);
                    outcome = prepared.Succeeded ? "Succeeded" : "Failed";
                    PublishSnapshot(new(prepared.Succeeded ? ApplicationInteractionPhase.Succeeded
                        : ApplicationInteractionPhase.Failed, input.Kind, transcription.Transcript,
                        prepared.Detail));
                    return;
                }
                outcome = "Unavailable";
                PublishSnapshot(new(ApplicationInteractionPhase.Failed, input.Kind,
                    transcription.Transcript, "Native UI interaction is not enabled yet."));
                return;
            }

            if (executionScope.Kind is ExecutionScopeKind.TextTransform or ExecutionScopeKind.Clarify)
            {
                outcome = "Clarify";
                var status = executionScope.Kind == ExecutionScopeKind.TextTransform
                    ? "Text transformation is not part of the current literal dictation or browser stage."
                    : executionScope.Detail ?? route.Detail ?? "The command needs clarification.";
                PublishSnapshot(new(ApplicationInteractionPhase.NeedsChoice, input.Kind,
                    transcription.Transcript, status,
                    [new InteractionChoice("retry", "Clarify request"), new InteractionChoice("cancel", "Cancel")],
                    InteractionCompletionState.Uncertain));
                return;
            }

            PublishSnapshot(new(ApplicationInteractionPhase.Observing, input.Kind,
                transcription.Transcript, "Collecting direct capabilities"));
            DecisionState? decisionState = null;
            if (_decisionEngine is not null || route.MediaOperation is not null)
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

                if (route.MediaOperation is { } mediaOperation)
                {
                    var plan = new VoicePlan(VoiceAction.MediaControl, Media: mediaOperation, Confidence: 1);
                    decision = new DecisionResult(plan, SemanticProgramPlanner.ToSingleStepProgram(plan),
                        new Dictionary<string, JevAnswer>(), 0, 0, 0);
                }
                else if (_decisionEngine is not null)
                {
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
                if (decision is not null)
                    _logger.LogInformation("Direct decision action_kind={ActionKind} media_op={MediaOp} snap_dir={SnapDir} target_app={TargetApp} target_window={TargetWindow} jev_ms={JevMs:F0}",
                        decision.Plan.Action,
                        decision.Plan.Action == VoiceAction.MediaControl ? decision.Plan.Media?.ToString() : null,
                        decision.Plan.Action == VoiceAction.SnapCurrentWindow ? decision.Plan.Snap?.ToString() : null,
                        decision.Plan.AppCandidateId,
                        decision.Plan.WindowCandidateId,
                        jevStart.HasValue && jevEnd.HasValue ? (jevEnd.Value - jevStart.Value).TotalMilliseconds : 0);
            }

            if (_programExecutor is not null && decision is not null && decisionState is not null)
            {
                var compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var compound)
                    && compound.QuestionType == "noul"
                    && compound.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;
                var program = decision.Program
                    ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));

                if (_commandRouter is not null
                    && program?.Steps.Any(static step => step is MediaControlStep) == true
                    && route.MediaRequestKind != MediaRequestKind.Transport)
                {
                    _logger.LogWarning("Direct media execution blocked: media_request_kind={MediaRequestKind}",
                        route.MediaRequestKind);
                    outcome = "Clarify";
                    PublishSnapshot(new(ApplicationInteractionPhase.NeedsChoice, input.Kind,
                        transcription.Transcript, "Media intent needs clarification before controlling current playback.",
                        [new InteractionChoice("retry", "Clarify request"), new InteractionChoice("cancel", "Cancel")],
                        InteractionCompletionState.Uncertain));
                    return;
                }

                if (program is not null)
                {
                    _logger.LogInformation("Direct executor actions={Actions}",
                        string.Join(",", program.Steps.Select(static step => step.GetType().Name)));
                    PublishSnapshot(new(ApplicationInteractionPhase.Executing, input.Kind,
                        transcription.Transcript, "Executing direct capability"));
                    var executionTimer = Stopwatch.StartNew();
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
                    executionTimer.Stop();
                    actionCount = programResult?.ExecutedCount ?? 0;
                    _logger.LogInformation("Direct executor outcome={Outcome} executed={Executed} latency_ms={LatencyMs:F0}",
                        programResult?.AllSucceeded == true ? "Succeeded" : "Failed",
                        actionCount, executionTimer.Elapsed.TotalMilliseconds);
                }
            }

            outcome = programResult?.AllSucceeded == true ? "Succeeded" : "Failed";
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
            LogPipelineSummary(result, activationId, lane, outcome, postSttStart, actionCount);
            PublishState(input.Kind, ActivationState.Idle);
        }
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
        _shutdown.Cancel();
        _commandHook.KeyDown -= OnCommandKeyDown;
        _commandHook.KeyUp -= OnCommandKeyUp;
        _dictationHook.KeyDown -= OnDictationKeyDown;
        _dictationHook.KeyUp -= OnDictationKeyUp;
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
        DateTimeOffset PipelineStart);
}
