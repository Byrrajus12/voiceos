using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Candidates;

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
    }

    public async Task<DecisionResult> DecideAsync(DecisionState state, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var request = BuildRequest(state);

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(request, options: JsonOptions);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Jev HTTP request failed");
            return ErrorResult(sw.Elapsed.TotalMilliseconds, $"HTTP error: {ex.Message}");
        }

        string body;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ErrorResult(sw.Elapsed.TotalMilliseconds, $"Failed to read response: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            sw.Stop();
            _logger.LogError("Jev returned {Status}: {Body}", (int)resp.StatusCode, body);
            return ErrorResult(sw.Elapsed.TotalMilliseconds, $"HTTP {(int)resp.StatusCode}");
        }

        sw.Stop();
        return ParseResponse(body, state, sw.Elapsed.TotalMilliseconds);
    }

    private DecisionResult ParseResponse(string body, DecisionState state, double durationMs)
    {
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
            {
                foreach (var prop in answersEl.EnumerateObject())
                    answers[prop.Name] = ParseAnswer(prop.Value);
            }

            var textCandidates = TextCandidateExtractor.Extract(state.Transcript);
            var plan = BuildPlan(answers, state, textCandidates);
            return new DecisionResult(plan, answers, durationMs, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Jev response");
            return ErrorResult(durationMs, $"Parse error: {ex.Message}");
        }
    }

    private static JevAnswer ParseAnswer(JsonElement el)
    {
        var type = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "choice" : "choice";

        var probs = new Dictionary<string, double>();
        string? selectedChoice = null;
        double confidence;

        if (type == "noul")
        {
            double noulProb = el.TryGetProperty("noul", out var np) ? np.GetDouble() : 0.0;
            probs["noul"] = noulProb;
            selectedChoice = noulProb >= 0.5 ? "true" : "false";
            confidence = el.TryGetProperty("confidence", out var confEl) ? confEl.GetDouble() : noulProb;
        }
        else
        {
            confidence = el.TryGetProperty("confidence", out var confEl) ? confEl.GetDouble() : 0.0;
            if (el.TryGetProperty("choice", out var choiceEl))
                selectedChoice = choiceEl.GetString();

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name is "type" or "choice" or "confidence") continue;
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    probs[prop.Name] = prop.Value.GetDouble();
            }
        }

        return new JevAnswer(type, selectedChoice, probs, confidence);
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
        if (answers.TryGetValue("target_app", out var appAnswer) && appAnswer.SelectedChoice != null)
        {
            var match = state.InstalledApps.FirstOrDefault(a => a.Id == appAnswer.SelectedChoice);
            appCandidate = match?.DisplayName ?? appAnswer.SelectedChoice;
            appCandidateId = match?.Id;
            appProcessName = match?.ProcessName;
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
            VoiceAction.SnapCurrentWindow;

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
                    ? WindowTargetMode.Named
                    : WindowTargetMode.Current;
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

        // ── Volume (deterministic extraction — VoiceOS-owned, not LLM-generated) ──
        int? volumeValue = null;
        if (action == VoiceAction.SetVolume &&
            VolumeExtractor.TryExtractPercent(state.Transcript, out int pct))
        {
            volumeValue = pct;
        }

        VolumeDirection? volumeAdjust = null;
        if (action == VoiceAction.AdjustVolume &&
            answers.TryGetValue("volume_direction", out var dirAnswer) &&
            dirAnswer.SelectedChoice != null &&
            dirAnswer.Confidence >= _actionThreshold &&
            Enum.TryParse<VolumeDirection>(dirAnswer.SelectedChoice, out VolumeDirection dir))
        {
            volumeAdjust = dir;
            confidence = Math.Min(confidence, dirAnswer.Confidence);
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
            ActivationMode: activationMode,
            WindowTargetMode: isWindowTargetedAction ? windowTargetMode : WindowTargetMode.Current);
    }

    public JevRequestDto BuildRequest(DecisionState state)
    {
        var textCandidates = TextCandidateExtractor.Extract(state.Transcript);
        var appCandidates = state.InstalledApps.ToDictionary(a => a.Id, a => a.DisplayName);
        var windowCandidates = state.OpenWindows.ToDictionary(w => w.Id, w => w.Title);

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
                    ["Previous"] = "Go to previous track (e.g. 'previous', 'go back', 'last song')",
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

            // Speculative: determines whether to focus an existing window or open a new instance.
            ["activation_mode"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to open or activate an application. Should it focus an existing window if one is available, or open a brand-new instance?",
                new Dictionary<string, string>
                {
                    ["FocusOrLaunch"] = "Activate or open: focus an existing window if one is open, launch if not. Default for 'open Chrome', 'go to Discord', 'bring up VS Code', 'open terminal'.",
                    ["NewInstance"] = "Explicitly request a new window or instance: 'new Chrome window', 'another VS Code window', 'open a new terminal', 'new instance of Discord'.",
                }),

            // Speculative: distinguishes "close this" (Current foreground) from "close Chrome" (Named app).
            ["window_target_mode"] = new JevQuestionDto(
                "choice",
                "Assume the user is issuing a window operation (close, maximize, minimize, snap). Is the target the current foreground window, or a specific named application window?",
                new Dictionary<string, string>
                {
                    ["Current"] = "User refers to the current or foreground window using words like 'this', 'the current window', 'it', 'the window'. No specific application name is mentioned as the target.",
                    ["Named"] = "User names a specific application as the target — 'Chrome', 'VS Code', 'Discord', 'the terminal', 'Google Chrome'. The command is directed at that particular named app's window.",
                }),
        };

        if (appCandidates.Count > 0)
        {
            questions["target_app"] = new JevQuestionDto(
                "choice",
                "Which application is the target of the command?",
                appCandidates);
        }

        if (windowCandidates.Count > 0)
        {
            questions["target_window"] = new JevQuestionDto(
                "choice",
                "Which open window is the target of the command?",
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

    private static Dictionary<string, string> BuildTextCriteria(IReadOnlyDictionary<string, string> candidates)
    {
        var criteria = new Dictionary<string, string>(candidates) { ["none"] = "No specific text content is needed" };
        return criteria;
    }

    private static DecisionResult ErrorResult(double durationMs, string reason)
        => new(new VoicePlan(VoiceAction.Rejected, RejectionReason: reason),
               new Dictionary<string, JevAnswer>(), durationMs, 0, 0);

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
