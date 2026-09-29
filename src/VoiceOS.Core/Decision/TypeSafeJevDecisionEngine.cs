using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Decision;

public sealed class TypeSafeJevDecisionEngine : IDecisionEngine
{
    private const string Endpoint = "https://api.typesafe.ai/v1/systemone";

    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly double _commandThreshold;
    private readonly double _actionThreshold;
    private readonly ILogger<TypeSafeJevDecisionEngine> _logger;

    public TypeSafeJevDecisionEngine(
        string apiKey,
        HttpClient http,
        string model,
        double commandThreshold,
        double actionThreshold,
        ILogger<TypeSafeJevDecisionEngine> logger)
    {
        _apiKey = apiKey;
        _http = http;
        _model = model;
        _commandThreshold = commandThreshold;
        _actionThreshold = actionThreshold;
        _logger = logger;
        _planner = new SemanticProgramPlanner(commandThreshold, actionThreshold);
    }

    public async Task<DecisionResult> DecideAsync(DecisionState state, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();

        // ── Pass 1: base request (is_command + single-action questions + is_compound) ──
        var buildBaseSw = Stopwatch.StartNew();
        var baseReq = BuildBaseRequest(state);
        buildBaseSw.Stop();

        var baseHttpSw = Stopwatch.StartNew();
        var baseResp = await SendAndParseAsync(baseReq, ct);
        baseHttpSw.Stop();

        if (baseResp == null)
        {
            totalSw.Stop();
            return ErrorResult(totalSw.Elapsed.TotalMilliseconds, "Base Jev request failed", providerFailed: true);
        }

        // ── Pass 2: compound-detail request (only when is_compound fires) ──────────────
        JevResponse? compoundResp = null;
        double buildCompoundMs = 0, compoundHttpMs = 0;

        bool isCompound = baseResp.Answers.TryGetValue("is_compound", out var icAns)
            && icAns.QuestionType == "noul"
            && icAns.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;

        if (isCompound)
        {
            var buildCompoundSw = Stopwatch.StartNew();
            var compoundReq = BuildCompoundRequest(state);
            buildCompoundSw.Stop();
            buildCompoundMs = buildCompoundSw.Elapsed.TotalMilliseconds;

            var compoundHttpSw = Stopwatch.StartNew();
            compoundResp = await SendAndParseAsync(compoundReq, ct);
            if (compoundResp is null)
                return ErrorResult(totalSw.Elapsed.TotalMilliseconds, "Compound Jev request failed", providerFailed: true);
            compoundHttpSw.Stop();
            compoundHttpMs = compoundHttpSw.Elapsed.TotalMilliseconds;
        }

        // Merge answers: base provides single-action context; compound overrides/adds unit_N_* answers
        var mergedAnswers = new Dictionary<string, JevAnswer>(baseResp.Answers);
        if (compoundResp != null)
            foreach (var kv in compoundResp.Answers)
                mergedAnswers[kv.Key] = kv.Value;

        int inputTokens = baseResp.InputTokens + (compoundResp?.InputTokens ?? 0);
        int outputTokens = baseResp.OutputTokens + (compoundResp?.OutputTokens ?? 0);

        var planSw = Stopwatch.StartNew();
        var result = ParseResponseFromAnswers(mergedAnswers, state, totalSw.Elapsed.TotalMilliseconds,
            inputTokens, outputTokens);
        planSw.Stop();

        totalSw.Stop();
        _logger.LogInformation(
            "Jev: buildBase={BuildBaseMs:F0}ms baseHttp={BaseHttpMs:F0}ms buildCompound={BuildCompoundMs:F0}ms compoundHttp={CompoundHttpMs:F0}ms plan={PlanMs:F0}ms",
            buildBaseSw.Elapsed.TotalMilliseconds, baseHttpSw.Elapsed.TotalMilliseconds,
            buildCompoundMs, compoundHttpMs, planSw.Elapsed.TotalMilliseconds);

        return result;
    }

    private async Task<JevResponse?> SendAndParseAsync(JevRequestDto request, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(request, options: JsonOptions);
        using var modelCall = Activation.LatencyTrace.Current?.BeginModelCall(
            request.Questions.ContainsKey("is_compound") ? "direct_base" : "direct_compound");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Jev HTTP request failed");
            return null;
        }

        string body;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to read Jev response body");
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Jev returned {Status}: {Body}", (int)resp.StatusCode, body);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int inputTokens = 0, outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
            }

            var answers = new Dictionary<string, JevAnswer>();
            if (root.TryGetProperty("answers", out var answersEl))
                foreach (var prop in answersEl.EnumerateObject())
                    answers[prop.Name] = JevAnswerParser.Parse(prop.Value);

            return new JevResponse(answers, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Jev response");
            return null;
        }
    }

    private sealed record JevResponse(
        Dictionary<string, JevAnswer> Answers,
        int InputTokens,
        int OutputTokens);

    private readonly SemanticProgramPlanner _planner;

    private DecisionResult ParseResponseFromAnswers(
        Dictionary<string, JevAnswer> answers,
        DecisionState state,
        double durationMs,
        int inputTokens,
        int outputTokens)
    {
        try
        {
            var textCandidates = TextCandidateExtractor.Extract(state.Transcript);
            foreach (var head in answers)
                JevDiagnostics.Log(_logger, "Direct head", head.Key, head.Value);
            var plan = BuildPlan(answers, state, textCandidates);
            var program = _planner.TryBuildProgram(answers, state, textCandidates);
            return new DecisionResult(plan, program, answers, durationMs, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build decision from Jev answers");
            return ErrorResult(durationMs, $"Parse error: {ex.Message}");
        }
    }

    private VoicePlan BuildPlan(
        Dictionary<string, JevAnswer> answers,
        DecisionState state,
        IReadOnlyDictionary<string, string> textCandidates)
    {
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            foreach (var kv in answers)
                _logger.LogDebug("  answer[{Key}] type={Type} selectedChoice={Choice} confidence={Conf:F3}",
                    kv.Key, kv.Value.QuestionType, kv.Value.SelectedChoice, kv.Value.Confidence);
        }

        if (!answers.TryGetValue("is_command", out var isCmd))
        {
            _logger.LogDebug("Jev gate: is_command missing → None");
            return new VoicePlan(VoiceAction.None, RejectionReason: "Not recognized as a command");
        }

        double isCmdProb = isCmd.QuestionType == "noul"
            ? isCmd.Probabilities.GetValueOrDefault("noul")
            : isCmd.Confidence;
        _logger.LogDebug("Jev gate: is_command noul={Prob:F3} threshold={Thr:F2}", isCmdProb, _commandThreshold);

        if (isCmdProb < _commandThreshold)
        {
            _logger.LogDebug("Jev gate: rejected – is_command noul={Prob:F3} below threshold {Thr:F2}",
                isCmdProb, _commandThreshold);
            return new VoicePlan(VoiceAction.None, RejectionReason: "Not recognized as a command");
        }

        if (!answers.TryGetValue("action_kind", out var kind) || kind.SelectedChoice == null)
        {
            _logger.LogDebug("Jev gate: action_kind missing or null → Rejected");
            return new VoicePlan(VoiceAction.Rejected, RejectionReason: "No action determined");
        }

        _logger.LogDebug("Jev gate: action_kind={Choice} confidence={Conf:F3} threshold={Thr:F2}",
            kind.SelectedChoice, kind.Confidence, _actionThreshold);

        if (!Enum.TryParse<VoiceAction>(kind.SelectedChoice, out var action) ||
            kind.Confidence < _actionThreshold)
        {
            _logger.LogDebug("Jev gate: rejected – low action confidence {Conf:F3}", kind.Confidence);
            return new VoicePlan(VoiceAction.Rejected,
                Confidence: kind.Confidence,
                RejectionReason: $"Low action confidence ({kind.Confidence:F2})");
        }

        double confidence = kind.Confidence;

        // ── App candidate ─────────────────────────────────────────────────────
        string? appCandidate = null;
        string? appCandidateId = null;
        string? appProcessName = null;
        string? appUserModelId = null;
        AppCandidate? appMatch = null;
        if (answers.TryGetValue("target_app", out var appAnswer) && appAnswer.SelectedChoice != null
            && appAnswer.SelectedChoice != NoAppChoice
            && (appMatch = state.InstalledApps.FirstOrDefault(a => a.Id == appAnswer.SelectedChoice)) is { } selectedApp)
        {
            appCandidate = selectedApp.DisplayName;
            appCandidateId = selectedApp.Id;
            appProcessName = selectedApp.ProcessName;
            appUserModelId = selectedApp.AppUserModelId;
            if (action == VoiceAction.OpenApp)
                confidence = Math.Min(confidence, appAnswer.Confidence);
        }

        // ── Window candidate + target mode ───────────────────────────────────
        // Resolution order matters:
        //   1. Speculatively resolve target_window (for possible Named use).
        //   2. Determine the authoritative target mode from window_target_mode.
        //   3. Enforce invariants:
        //      Current  → windowCandidateId MUST be null (speculative answer discarded).
        //      Named    → windowCandidateId required or RequiresClarification.
        //      Uncertain→ RequiresClarification; windowCandidateId discarded.

        JevAnswer? winAnswer = answers.TryGetValue("target_window", out var wa) ? wa : null;
        string? windowCandidate = null;
        string? windowCandidateId = null;

        bool isWindowTargetedAction = action is
            VoiceAction.FocusWindow or
            VoiceAction.CloseCurrentWindow or
            VoiceAction.MaximizeCurrentWindow or
            VoiceAction.MinimizeCurrentWindow or
            VoiceAction.SnapCurrentWindow or
            VoiceAction.MoveWindow;

        // Step 1: Speculative resolution — only used when mode is confirmed Named.
        if (isWindowTargetedAction && winAnswer?.SelectedChoice != null &&
            winAnswer.Confidence >= _actionThreshold)
        {
            var match = state.OpenWindows.FirstOrDefault(w => w.Id == winAnswer.SelectedChoice);
            windowCandidate = match?.Title ?? winAnswer.SelectedChoice;
            windowCandidateId = match?.Id;
            if (windowCandidateId != null)
                confidence = Math.Min(confidence, winAnswer.Confidence);
        }

        // Step 2: Determine target mode.
        // FocusWindow is inherently Named (explicit switch-to verb).
        // Close/Max/Min/Snap: Jev answers window_target_mode.
        //   Missing or low-confidence → uncertain (RequiresClarification; never default to foreground).
        WindowTargetMode windowTargetMode = WindowTargetMode.Current;
        bool windowTargetModeUncertain = false;
        bool referentMode = false;

        if (action == VoiceAction.FocusWindow)
        {
            windowTargetMode = WindowTargetMode.Named;
        }
        else if (isWindowTargetedAction)
        {
            if (!answers.TryGetValue("window_target_mode", out var wtmAnswer) ||
                wtmAnswer.SelectedChoice == null ||
                wtmAnswer.Confidence < _actionThreshold)
            {
                // Missing or low-confidence mode answer — semantics are ambiguous.
                // Never silently assume Current (foreground) or Named.
                windowTargetModeUncertain = true;
            }
            else
            {
                windowTargetMode = wtmAnswer.SelectedChoice == "Named"
                    || wtmAnswer.SelectedChoice == ReferentMode && state.ReferentWindowIds is { Count: > 0 }
                    ? WindowTargetMode.Named
                    : WindowTargetMode.Current;
                referentMode = wtmAnswer.SelectedChoice == ReferentMode
                    && state.ReferentWindowIds is { Count: > 0 };
            }
        }

        // A reference back to earlier work is honored only for a window VoiceOS itself established and that
        // still exists. A pick outside that set is not a reference; a single candidate needs no pick.
        if (referentMode)
        {
            var known = state.ReferentWindowIds!;
            // Acting on a coin flip between windows is worse than asking: a referent pick needs a firm answer
            // whenever it is one of several.
            if (known.Count > 1 && (winAnswer?.Confidence ?? 0) < ReferentPickFloor) windowCandidateId = null;
            if (windowCandidateId is null || !known.Contains(windowCandidateId))
            {
                windowCandidateId = known.Count == 1 ? known[0] : null;
                windowCandidate = windowCandidateId is null ? null
                    : state.OpenWindows.FirstOrDefault(w => w.Id == windowCandidateId)?.Title;
                if (windowCandidateId is null) windowTargetModeUncertain = true;
            }
        }

        // Step 3: Enforce invariants.
        // Current and Uncertain modes must never carry an actionable named candidate.
        // Discarding speculatively resolved data ensures execution sees only what the plan semantics allow.
        if (windowTargetModeUncertain || windowTargetMode == WindowTargetMode.Current)
        {
            windowCandidate = null;
            windowCandidateId = null;
        }

        // ── Text candidate ────────────────────────────────────────────────────
        string? textContent = null;
        if (answers.TryGetValue("text", out var textAnswer) &&
            textAnswer.SelectedChoice != null &&
            textAnswer.SelectedChoice != "none")
        {
            textCandidates.TryGetValue(textAnswer.SelectedChoice, out textContent);
        }

        // ── Media operation ───────────────────────────────────────────────────
        MediaOperation? mediaOp = null;
        if (action == VoiceAction.MediaControl &&
            answers.TryGetValue("media_op", out var mediaAnswer) &&
            mediaAnswer.SelectedChoice != null &&
            Enum.TryParse<MediaOperation>(mediaAnswer.SelectedChoice, out var mo))
        {
            mediaOp = mo;
            confidence = Math.Min(confidence, mediaAnswer.Confidence);
        }

        // ── Snap direction ────────────────────────────────────────────────────
        SnapDirection? snapDir = null;
        if (action == VoiceAction.SnapCurrentWindow &&
            answers.TryGetValue("snap_dir", out var snapAnswer) &&
            snapAnswer.SelectedChoice != null &&
            Enum.TryParse<SnapDirection>(snapAnswer.SelectedChoice, out var sd))
        {
            snapDir = sd;
            confidence = Math.Min(confidence, snapAnswer.Confidence);
        }

        // ── Monitor target (MoveWindow) ───────────────────────────────────────
        MonitorTarget? monitorTarget = null;
        if (action == VoiceAction.MoveWindow &&
            answers.TryGetValue("monitor_target", out var monitorAnswer) &&
            monitorAnswer.SelectedChoice != null)
        {
            monitorTarget = SemanticProgramPlanner.ParseMonitorTargetPublic(monitorAnswer.SelectedChoice);
            if (monitorTarget != null)
                confidence = Math.Min(confidence, monitorAnswer.Confidence);
        }

        // ── Volume (deterministic extraction — VoiceOS-owned, not LLM-generated) ──
        int? volumeValue = null;
        if (action == VoiceAction.SetVolume &&
            VolumeExtractor.TryExtractPercent(state.Transcript, out int pct))
        {
            volumeValue = pct;
        }

        VolumeDirection? volumeAdjust = null;
        int? volumeAdjustAmount = null;
        if (action == VoiceAction.AdjustVolume &&
            answers.TryGetValue("volume_direction", out var dirAnswer) &&
            dirAnswer.SelectedChoice != null &&
            dirAnswer.Confidence >= _actionThreshold &&
            Enum.TryParse<VolumeDirection>(dirAnswer.SelectedChoice, out VolumeDirection dir))
        {
            volumeAdjust = dir;
            confidence = Math.Min(confidence, dirAnswer.Confidence);
            if (VolumeExtractor.TryExtractPercent(state.Transcript, out int adjAmt))
                volumeAdjustAmount = adjAmt;
        }

        // ── Activation mode (OpenApp only) ────────────────────────────────────
        AppActivationMode activationMode = AppActivationMode.FocusOrLaunch;
        if (action == VoiceAction.OpenApp &&
            answers.TryGetValue("activation_mode", out var modeAnswer) &&
            modeAnswer.SelectedChoice != null &&
            Enum.TryParse<AppActivationMode>(modeAnswer.SelectedChoice, out var parsedMode))
        {
            activationMode = parsedMode;
        }

        // ── Completeness (code-owned per-action) ──────────────────────────────
        bool requiresClarification = action switch
        {
            VoiceAction.OpenApp => appCandidate == null,
            // FocusWindow is always Named — requires a confident resolved target
            VoiceAction.FocusWindow => windowCandidate == null ||
                (winAnswer?.Confidence ?? 0) < _actionThreshold,
            VoiceAction.MediaControl => mediaOp == null,
            // Snap: needs direction; uncertain mode or Named-without-candidate also block execution
            VoiceAction.SnapCurrentWindow => snapDir == null || windowTargetModeUncertain ||
                (windowTargetMode == WindowTargetMode.Named && windowCandidateId == null),
            // MoveWindow: needs monitor target; uncertain mode or Named-without-candidate also block
            VoiceAction.MoveWindow => monitorTarget == null || windowTargetModeUncertain ||
                (windowTargetMode == WindowTargetMode.Named && windowCandidateId == null),
            VoiceAction.SetVolume => volumeValue == null,
            VoiceAction.AdjustVolume => volumeAdjust == null,
            // Close/Maximize/Minimize: uncertain mode blocks; Named requires a resolved candidate
            VoiceAction.CloseCurrentWindow or
            VoiceAction.MaximizeCurrentWindow or
            VoiceAction.MinimizeCurrentWindow =>
                windowTargetModeUncertain ||
                (windowTargetMode == WindowTargetMode.Named && windowCandidateId == null),
            _ => false,
        };

        _logger.LogDebug(
            "Jev gate: approved action={Action} confidence={Conf:F3} requiresClarification={Rc}",
            action, confidence, requiresClarification);

        // For Named-mode window ops, carry the app candidate ID so PlanToStep can use AppTarget
        // (enabling execution-time ambiguity detection at the shared resolution boundary).
        // The app is used only when its identity corroborates the resolved window; an unrelated
        // forced app choice must never replace the exact resolved window.
        string? windowAppCandidateId = null;
        if (isWindowTargetedAction && windowTargetMode == WindowTargetMode.Named && !referentMode
            && AppCorroboratesWindow(appMatch, state.OpenWindows.FirstOrDefault(w => w.Id == windowCandidateId)))
            windowAppCandidateId = appCandidateId;

        return new VoicePlan(
            action,
            AppCandidate: action is VoiceAction.OpenApp ? appCandidate : null,
            WindowCandidate: isWindowTargetedAction ? windowCandidate : null,
            TextContent: textContent,
            Media: mediaOp,
            Snap: snapDir,
            VolumeValue: volumeValue,
            VolumeAdjust: volumeAdjust,
            Confidence: confidence,
            RequiresClarification: requiresClarification,
            AppCandidateId: action is VoiceAction.OpenApp ? appCandidateId : null,
            WindowCandidateId: isWindowTargetedAction ? windowCandidateId : null,
            AppProcessName: action is VoiceAction.OpenApp ? appProcessName : null,
            AppUserModelId: action is VoiceAction.OpenApp ? appUserModelId : null,
            ActivationMode: activationMode,
            WindowTargetMode: isWindowTargetedAction ? windowTargetMode : WindowTargetMode.Current,
            MonitorMove: action is VoiceAction.MoveWindow ? monitorTarget : null,
            WindowAppCandidateId: windowAppCandidateId,
            VolumeAdjustAmount: volumeAdjustAmount);
    }

    internal const string ReferentMode = "Referent";
    private const double ReferentPickFloor = 0.7;

    /// <summary>Typed order of establishment, so "the first one" and "the last one" have something to be matched against.</summary>
    private static string RecencyLabel(IReadOnlyList<string> mostRecentFirst, string id)
    {
        var index = mostRecentFirst.ToList().IndexOf(id);
        if (mostRecentFirst.Count == 1) return "the only such window";
        if (index == 0) return "newest";
        return index == mostRecentFirst.Count - 1 ? "oldest" : $"#{index + 1} newest";
    }

    private static Dictionary<string, string> WindowModeCriteria(bool referentOffered)
    {
        var criteria = new Dictionary<string, string>
        {
            ["Current"] = referentOffered
                ? "User refers to the current or foreground window using words like 'this', 'the current window', 'the window'. No specific application name is mentioned as the target."
                : "User refers to the current or foreground window using words like 'this', 'the current window', 'it', 'the window'. No specific application name is mentioned as the target.",
            ["Named"] = "User names a specific application as the target — 'Chrome', 'VS Code', 'Discord', 'the terminal', 'Google Chrome'. The command is directed at that particular named app's window."
        };
        if (referentOffered)
            criteria[ReferentMode] = "User refers back to a window VoiceOS recently opened or used, with a pronoun or description such as 'it', 'that one', 'the one I just opened', without naming an app. Only windows marked as recently opened or used by VoiceOS qualify. Never use for 'this' or 'the current window'.";
        return criteria;
    }

    /// <summary>The target_app choice key meaning no offered app is the target. A forced choice
    /// over installed apps must never manufacture an executable target by itself.</summary>
    public const string NoAppChoice = "none";

    internal static Dictionary<string, string> AppChoices(DecisionState state)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var app in state.InstalledApps)
            if (app.Id != NoAppChoice) choices.TryAdd(app.Id, app.DisplayName);
        if (choices.Count > 0)
            choices[NoAppChoice] = "No offered application is the named target; the requested app is not installed or not listed.";
        return choices;
    }

    /// <summary>A named window operation may carry an app target only when that app's identity
    /// corroborates the already-resolved window; otherwise the resolved window itself is the target.</summary>
    internal static bool AppCorroboratesWindow(AppCandidate? app, WindowCandidate? window)
    {
        if (app is null || window is null) return false;
        if (app.AppUserModelId is not null && window.AppUserModelId is not null)
            return string.Equals(app.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        return app.ProcessName is not null
            && string.Equals(app.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] Ordinals = ["first", "second", "third", "fourth"];
    private static string Ordinal(int i) => i >= 1 && i <= 4 ? Ordinals[i - 1] : i.ToString();

    /// <summary>
    /// Builds the base request: single-action questions plus is_compound routing signal.
    /// Does NOT include unit_count or unit_N_* questions — those are in BuildCompoundRequest.
    /// Sent for every utterance; drives both simple commands and compound routing.
    /// </summary>
    public JevRequestDto BuildBaseRequest(DecisionState state)
    {
        var textCandidates = TextCandidateExtractor.Extract(state.Transcript);
        var appCandidates = AppChoices(state);
        var referentWindows = state.ReferentWindowIds ?? [];
        var windowCandidates = state.OpenWindows.ToDictionary(w => w.Id,
            w => referentWindows.Contains(w.Id)
                ? $"{w.Title} (recently opened or used by VoiceOS, {RecencyLabel(referentWindows, w.Id)})" : w.Title);

        var questions = new Dictionary<string, JevQuestionDto>
        {
            ["is_command"] = new JevQuestionDto(
                "noul",
                "Is `utterance` a voice command spoken to a Windows voice assistant that controls the OS? Return false for casual conversation, thinking aloud, background chatter, or speech not directed at the computer.",
                null),

            ["action_kind"] = new JevQuestionDto(
                "choice",
                "The user spoke a voice command to Windows. `utterance` is the transcript. Which single kind of OS action do they intend?",
                new Dictionary<string, string>
                {
                    ["None"] = "Not a command for the computer: casual conversation, thinking aloud, background chatter, or unintelligible speech",
                    ["OpenApp"] = "Activate or open an application — focus an existing window if available, or launch it. Use for 'open Chrome', 'pull up VS Code', 'bring up Discord', 'go to Spotify'. Do NOT use when the intent is to close, quit, exit, or dismiss an app.",
                    ["FocusWindow"] = "Switch focus to a specific already-open window using an explicit switch/go/bring verb — 'switch to Chrome', 'go to VS Code', 'bring my terminal to front'. Use when explicitly switching between known-open windows.",
                    ["CloseCurrentWindow"] = "Close or quit a window — either the focused one ('close this', 'quit', 'exit', 'close the window', 'shut this') or a specifically named one ('close Chrome', 'quit VS Code', 'exit the terminal', 'close Google Chrome'). The intent must be dismissal or closing, not launching or switching.",
                    ["MaximizeCurrentWindow"] = "Maximize or make fullscreen a window — the current one ('maximize this', 'make it fullscreen', 'make this bigger') or a named one ('maximize Chrome', 'fullscreen VS Code').",
                    ["MinimizeCurrentWindow"] = "Minimize or hide a window — the current one ('minimize this', 'hide this', 'send it to the taskbar') or a named one ('minimize Chrome', 'hide VS Code', 'send Discord to taskbar').",
                    ["SnapCurrentWindow"] = "Snap or tile a window to a side of the screen — the current one ('snap left', 'snap this right') or a named one ('snap Chrome to the left', 'snap VS Code right', 'tile Discord on the left').",
                    ["MoveWindow"] = "Move a window to a different monitor or display — 'move Chrome to the other monitor', 'move this to my laptop screen', 'put VS Code on the external display', 'move this to the monitor on the left', 'send this to the other screen'.",
                    ["MediaControl"] = "Control music or video playback: play, pause, resume, stop, next track, skip, previous track (e.g. 'pause this', 'skip this', 'next song', 'play', 'stop the music', 'previous track')",
                    ["SetVolume"] = "Set system volume to a specific level or percentage (e.g. 'set volume to 50', 'make it 30 percent', 'volume at 80', 'volume fifteen')",
                    ["AdjustVolume"] = "Increase or decrease system volume without a specific target level (e.g. 'turn it up', 'louder', 'volume down', 'a bit quieter', 'increase volume', 'lower the volume')",
                }),

            ["text"] = new JevQuestionDto(
                "choice",
                "Assume the user wants some text typed or used. `candidates` holds possible payloads extracted from the utterance. Which candidate is the intended payload, with no command words included?",
                BuildTextCriteria(textCandidates)),

            ["media_op"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to control media playback. Which operation do they want?",
                new Dictionary<string, string>
                {
                    ["Play"] = "Start or resume playback",
                    ["Pause"] = "Pause or stop playback (e.g. 'pause', 'pause this', 'stop')",
                    ["Toggle"] = "Toggle play/pause state",
                    ["Next"] = "Skip to next track (e.g. 'next', 'skip this', 'next song', 'skip')",
                    ["Previous"] = "Go to previous track (e.g. 'previous track', 'previous song', 'last song')",
                }),

            ["snap_dir"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to snap a window. Which direction?",
                new Dictionary<string, string>
                {
                    ["Left"] = "Snap to left half of the screen",
                    ["Right"] = "Snap to right half of the screen",
                }),

            ["volume_direction"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to adjust volume without specifying a level. Which direction do they want the volume to change?",
                new Dictionary<string, string>
                {
                    ["Up"] = "Increase volume: turn it up, louder, raise the volume, boost the sound, increase volume, make it louder, higher",
                    ["Down"] = "Decrease volume: turn it down, quieter, lower the volume, reduce volume, decreased volume, softer, bring the sound down, less",
                }),

            ["activation_mode"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to open or activate an application. Should it focus an existing window if one is available, or open a brand-new instance?",
                new Dictionary<string, string>
                {
                    ["FocusOrLaunch"] = "Activate or open: focus an existing window if one is open, launch if not. Default for 'open Chrome', 'go to Discord', 'bring up VS Code', 'open terminal'.",
                    ["NewInstance"] = "Explicitly request a new window or instance: 'new Chrome window', 'another VS Code window', 'open a new terminal', 'new instance of Discord'.",
                }),

            ["window_target_mode"] = new JevQuestionDto(
                "choice",
                "Assume the user is issuing a window operation (close, maximize, minimize, snap). Is the target the current foreground window, or a specific named application window?",
                WindowModeCriteria(referentWindows.Count > 0)),

            ["is_compound"] = new JevQuestionDto(
                "noul",
                "Does `utterance` contain two or more independent action units that should be executed sequentially? " +
                "Return true for 'Open Chrome and minimize Discord', 'Snap Chrome right and VS Code left', " +
                "'Open Chrome, turn the volume down, and minimize Discord'. Return false for a single action.",
                null),

            ["monitor_target"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to move a window to a different monitor. Which monitor is the target?",
                new Dictionary<string, string>
                {
                    ["Primary"]  = "The primary or main monitor",
                    ["Current"]  = "The monitor the window is currently on (same display, current monitor)",
                    ["Other"]    = "The other monitor when exactly two monitors are connected (the other one, the other screen, the other display)",
                    ["Internal"] = "The laptop's built-in or internal display (laptop screen, built-in display, laptop display, notebook screen, internal monitor)",
                    ["External"] = "An external display connected via HDMI, DisplayPort, etc. (external monitor, external display, the external screen, my monitor, my external monitor, my external display, my screen)",
                    ["Left"]     = "The monitor physically to the left of the current one",
                    ["Right"]    = "The monitor physically to the right of the current one",
                    ["Above"]    = "The monitor physically above the current one",
                    ["Below"]    = "The monitor physically below the current one",
                    ["UnsupportedExplicitTarget"] = "A monitor reference using a numeric position, ordinal, or explicit name that cannot be deterministically grounded — such as 'monitor 2', 'second monitor', 'display 1', 'screen number 3', or any other numbered or positionally-indexed display identifier. VoiceOS cannot safely resolve these.",
                }),
        };

        if (appCandidates.Count > 0)
        {
            questions["target_app"] = new JevQuestionDto(
                "choice",
                "Which application is the target of the command? Choose an offered app only when it is the application the user names or refers to. Choose none when the named application is not offered; never substitute a different app.",
                appCandidates);
        }

        if (windowCandidates.Count > 0)
        {
            questions["target_window"] = new JevQuestionDto(
                "choice",
                referentWindows.Count == 0
                    ? "Which open window is the target of the command?"
                    : "Which open window is the target of the command? Windows marked as recently opened or used by VoiceOS carry their order of use (newest, oldest); a reference back to earlier work denotes one of those, and a reference to the first or earliest one means the oldest.",
                windowCandidates);
        }

        return new JevRequestDto(
            State: new JevStateDto(
                Utterance: state.Transcript,
                FrontmostApp: state.ForegroundApp,
                Apps: state.InstalledApps.Select(a => a.DisplayName).ToList(),
                Candidates: new Dictionary<string, string>(textCandidates)),
            Model: _model,
            Questions: questions);
    }

    /// <summary>
    /// Builds the compound-detail request: unit_count + unit_N_* questions only.
    /// Sent only when the base request confirms is_compound. Never sent for simple commands.
    /// </summary>
    public JevRequestDto BuildCompoundRequest(DecisionState state)
    {
        var appCandidates = AppChoices(state);
        var textCandidates = TextCandidateExtractor.Extract(state.Transcript);

        var questions = new Dictionary<string, JevQuestionDto>
        {
            ["unit_count"] = new JevQuestionDto(
                "choice",
                "How many distinct, independent action units does `utterance` contain? Each unit is a separate action the user wants performed.",
                new Dictionary<string, string>
                {
                    ["1"] = "A single action (Open Chrome, Snap left, Turn volume down, Pause)",
                    ["2"] = "Exactly two actions (Open Chrome and minimize Discord, Snap Chrome right and VS Code left)",
                    ["3"] = "Exactly three actions (Open Chrome, turn the volume down, and minimize Discord)",
                    ["4"] = "Four or more actions",
                }),
        };

        var actionKindChoices = new Dictionary<string, string>
        {
            ["None"] = "This slot does not represent a valid action unit (utterance has fewer units than this position)",
            ["OpenApp"] = "Activate or open an application",
            ["FocusWindow"] = "Switch focus to a specific named window",
            ["CloseCurrentWindow"] = "Close or quit a window",
            ["MaximizeCurrentWindow"] = "Maximize or fullscreen a window",
            ["MinimizeCurrentWindow"] = "Minimize or hide a window",
            ["SnapCurrentWindow"] = "Snap or tile a window to a screen side",
            ["MoveWindow"] = "Move a window to a different monitor or display",
            ["MediaControl"] = "Control media playback (play, pause, skip, etc.)",
            ["SetVolume"] = "Set system volume to a specific level",
            ["AdjustVolume"] = "Increase or decrease system volume",
        };

        var monitorTargetChoices = new Dictionary<string, string>
        {
            ["Primary"]  = "The primary or main monitor",
            ["Current"]  = "The monitor the window is currently on",
            ["Other"]    = "The other monitor (two-monitor setup, not an ordinal reference)",
            ["Internal"] = "The laptop's built-in or internal display",
            ["External"] = "An external display connected via cable — natural references like 'my monitor', 'my external monitor', 'my external display', or 'my screen' map here (not 'second monitor' or any ordinal)",
            ["Left"]     = "The monitor to the left of the current one",
            ["Right"]    = "The monitor to the right of the current one",
            ["Above"]    = "The monitor above the current one",
            ["Below"]    = "The monitor below the current one",
            ["UnsupportedExplicitTarget"] = "Numeric or ordinal monitor reference ('monitor 2', 'second monitor', 'display 1') — cannot be safely grounded",
        };

        var windowTargetModeChoices = new Dictionary<string, string>
        {
            ["Current"] = "The current foreground window ('this', 'it', 'the window') — no specific app named",
            ["Named"] = "A specific named application window ('Chrome', 'VS Code', 'Discord')",
        };

        var snapDirChoices = new Dictionary<string, string>
        {
            ["Left"] = "Snap to left half",
            ["Right"] = "Snap to right half",
        };

        var activationModeChoices = new Dictionary<string, string>
        {
            ["FocusOrLaunch"] = "Focus existing window or launch if not running",
            ["NewInstance"] = "Open a new instance ('new Chrome window', 'another VS Code')",
        };

        var volumeDirChoices = new Dictionary<string, string>
        {
            ["Up"] = "Increase volume (louder, turn it up, raise)",
            ["Down"] = "Decrease volume (quieter, turn it down, lower)",
        };

        var mediaOpChoices = new Dictionary<string, string>
        {
            ["Play"] = "Start or resume playback",
            ["Pause"] = "Pause playback",
            ["Toggle"] = "Toggle play/pause",
            ["Next"] = "Skip to next track",
            ["Previous"] = "Go to previous track",
        };

        for (int i = 1; i <= SemanticProgramPlanner.MaxUnits; i++)
        {
            var ord = Ordinal(i);
            var ifFewer = i > 1 ? $" If fewer than {i} units exist in the utterance, choose None." : "";

            questions[$"unit_{i}_action_kind"] = new JevQuestionDto(
                "choice",
                $"Assume `utterance` contains multiple independent actions. What is the action kind of the {ord} action unit?{ifFewer}",
                actionKindChoices);

            questions[$"unit_{i}_window_target_mode"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance`: is the window/app target the current foreground window, or a specific named application?",
                windowTargetModeChoices);

            questions[$"unit_{i}_snap_dir"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance` (assume it is a snap/tile action): which direction?",
                snapDirChoices);

            questions[$"unit_{i}_activation_mode"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance` (assume it is an open/activate action): focus existing or open new instance?",
                activationModeChoices);

            questions[$"unit_{i}_volume_direction"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance` (assume it is a volume-adjustment action): which direction?",
                volumeDirChoices);

            questions[$"unit_{i}_media_op"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance` (assume it is a media-control action): which operation?",
                mediaOpChoices);

            if (i > 1)
            {
                questions[$"unit_{i}_prior_ref"] = new JevQuestionDto(
                    "noul",
                    $"For the {ord} action unit in `utterance`: does its target explicitly reference the RESULT of a prior action unit " +
                    $"using a pronoun or reference like 'it', 'that', 'the one you just opened'? " +
                    $"Return true only for clear referential language pointing to the output of an earlier step.",
                    null);
            }

            questions[$"unit_{i}_monitor_target"] = new JevQuestionDto(
                "choice",
                $"For the {ord} action unit in `utterance` (assume it is a move-to-monitor action): which monitor is the target?",
                monitorTargetChoices);

            if (appCandidates.Count > 0)
            {
                questions[$"unit_{i}_target_app"] = new JevQuestionDto(
                    "choice",
                    $"Which application is the target of the {ord} action unit in `utterance`? Choose none when the named application is not offered; never substitute a different app.",
                    appCandidates);
            }
        }

        return new JevRequestDto(
            State: new JevStateDto(
                Utterance: state.Transcript,
                FrontmostApp: state.ForegroundApp,
                Apps: state.InstalledApps.Select(a => a.DisplayName).ToList(),
                Candidates: new Dictionary<string, string>(textCandidates)),
            Model: _model,
            Questions: questions);
    }

    private static Dictionary<string, string> BuildTextCriteria(IReadOnlyDictionary<string, string> candidates)
    {
        var criteria = new Dictionary<string, string>(candidates) { ["none"] = "No specific text content is needed" };
        return criteria;
    }

    private static DecisionResult ErrorResult(double durationMs, string reason, bool providerFailed = false)
        => new(new VoicePlan(VoiceAction.Rejected, RejectionReason: reason),
               null,
               new Dictionary<string, JevAnswer>(), durationMs, 0, 0) { ProviderFailed = providerFailed };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// Request DTOs
public sealed record JevRequestDto(
    [property: JsonPropertyName("state")] JevStateDto State,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("questions")] Dictionary<string, JevQuestionDto> Questions);

public sealed record JevStateDto(
    [property: JsonPropertyName("utterance")] string Utterance,
    [property: JsonPropertyName("frontmost_app")] string FrontmostApp,
    [property: JsonPropertyName("apps")] List<string> Apps,
    [property: JsonPropertyName("candidates")] Dictionary<string, string> Candidates);

public sealed record JevQuestionDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("criteria")] Dictionary<string, string>? Criteria);
