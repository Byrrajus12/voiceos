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
            // noul answers return a single calibrated yes-probability in the "noul" field.
            // It is NOT a choice result; do not read "true"/"false"/"choice" sub-properties.
            double noulProb = el.TryGetProperty("noul", out var np) ? np.GetDouble() : 0.0;
            probs["noul"] = noulProb;
            selectedChoice = noulProb >= 0.5 ? "true" : "false";
            // The noul scalar is the calibrated probability; treat it as confidence unless
            // the API provides an explicit confidence field.
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
        _logger.LogDebug("Jev gate: is_command noul={Prob:F3} threshold={Thr:F2}",
            isCmdProb, _commandThreshold);

        if (isCmdProb < _commandThreshold)
        {
            _logger.LogDebug("Jev gate: rejected – is_command noul={Prob:F3} below threshold {Thr:F2}",
                isCmdProb, _commandThreshold);
            return new VoicePlan(VoiceAction.None,
                RejectionReason: "Not recognized as a command");
        }

        if (!answers.TryGetValue("action_kind", out var kind) ||
            kind.SelectedChoice == null)
        {
            _logger.LogDebug("Jev gate: action_kind missing or null → Rejected");
            return new VoicePlan(VoiceAction.Rejected,
                RejectionReason: "No action determined");
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

        // Confidence starts at action_kind confidence.
        // Only fold in answers that are actually required for the chosen action.
        // Speculative answers for other actions must never lower plan confidence.
        double confidence = kind.Confidence;

        // Resolve target_app for any action that might need it, but only include
        // its confidence when the chosen action actually requires an app target.
        string? appCandidate = null;
        if (answers.TryGetValue("target_app", out var appAnswer) && appAnswer.SelectedChoice != null)
        {
            var match = state.InstalledApps.FirstOrDefault(a => a.Id == appAnswer.SelectedChoice);
            appCandidate = match?.DisplayName ?? appAnswer.SelectedChoice;
            if (action == VoiceAction.OpenApp)
            {
                confidence = Math.Min(confidence, appAnswer.Confidence);
                _logger.LogDebug("Jev gate: target_app={Choice} confidence={Conf:F3} resolved={App}",
                    appAnswer.SelectedChoice, appAnswer.Confidence, appCandidate);
            }
        }

        // Resolve target_window; only include its confidence for FocusWindow.
        string? windowCandidate = null;
        if (answers.TryGetValue("target_window", out var winAnswer) && winAnswer.SelectedChoice != null)
        {
            var match = state.OpenWindows.FirstOrDefault(w => w.Id == winAnswer.SelectedChoice);
            windowCandidate = match?.Title ?? winAnswer.SelectedChoice;
            if (action == VoiceAction.FocusWindow)
                confidence = Math.Min(confidence, winAnswer.Confidence);
        }

        // Resolve text candidate; no V0 action requires it in confidence yet.
        string? textContent = null;
        if (answers.TryGetValue("text", out var textAnswer) &&
            textAnswer.SelectedChoice != null &&
            textAnswer.SelectedChoice != "none")
        {
            textCandidates.TryGetValue(textAnswer.SelectedChoice, out textContent);
        }

        MediaOperation? mediaOp = null;
        if (action == VoiceAction.MediaControl &&
            answers.TryGetValue("media_op", out var mediaAnswer) &&
            mediaAnswer.SelectedChoice != null &&
            Enum.TryParse<MediaOperation>(mediaAnswer.SelectedChoice, out var mo))
        {
            mediaOp = mo;
            confidence = Math.Min(confidence, mediaAnswer.Confidence);
        }

        SnapDirection? snapDir = null;
        if (action == VoiceAction.SnapCurrentWindow &&
            answers.TryGetValue("snap_dir", out var snapAnswer) &&
            snapAnswer.SelectedChoice != null &&
            Enum.TryParse<SnapDirection>(snapAnswer.SelectedChoice, out var sd))
        {
            snapDir = sd;
            confidence = Math.Min(confidence, snapAnswer.Confidence);
        }

        // Completeness is determined per-action in code, not by a generic Jev question.
        // An action is complete when every required argument is present.
        bool requiresClarification = action switch
        {
            VoiceAction.OpenApp => appCandidate == null,
            VoiceAction.FocusWindow => windowCandidate == null,
            VoiceAction.MediaControl => mediaOp == null,
            VoiceAction.SnapCurrentWindow => snapDir == null,
            // CloseCurrentWindow/MaximizeCurrentWindow/MinimizeCurrentWindow operate on the
            // current window and need no additional target — always complete.
            _ => false,
        };

        _logger.LogDebug("Jev gate: plan approved action={Action} confidence={Conf:F3} requiresClarification={Rc}",
            action, confidence, requiresClarification);

        return new VoicePlan(
            action,
            AppCandidate: action is VoiceAction.OpenApp ? appCandidate : null,
            WindowCandidate: action is VoiceAction.FocusWindow ? windowCandidate : null,
            TextContent: textContent,
            Media: mediaOp,
            Snap: snapDir,
            Confidence: confidence,
            RequiresClarification: requiresClarification);
    }

    public JevRequestDto BuildRequest(DecisionState state)
    {
        // Extract text candidates from transcript (c0, c1, …)
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
                    ["OpenApp"] = "Launch, open, pull up, or bring up an application that may not currently be running (e.g. 'open Chrome', 'pull up Spotify', 'launch VS Code', 'bring up notepad')",
                    ["FocusWindow"] = "Switch focus to or bring to front a window that is already open (e.g. 'switch to VS Code', 'go to Chrome', 'bring up my terminal'). Use this when the app is likely running.",
                    ["CloseCurrentWindow"] = "Close the currently focused window (e.g. 'close this', 'close the window', 'shut this')",
                    ["MaximizeCurrentWindow"] = "Maximize or fullscreen the currently focused window (e.g. 'maximize this', 'make it fullscreen', 'make this bigger')",
                    ["MinimizeCurrentWindow"] = "Minimize or hide the currently focused window (e.g. 'minimize this', 'hide this window', 'send it to the taskbar')",
                    ["SnapCurrentWindow"] = "Snap or tile the current window to a side of the screen (e.g. 'snap left', 'move this to the right side', 'snap this to the right')",
                    ["MediaControl"] = "Control music or video playback: play, pause, resume, stop, next track, skip, previous track (e.g. 'pause this', 'skip this', 'next song', 'play', 'stop the music', 'previous track')",
                    ["SetVolume"] = "Set system volume to a specific level or percentage (e.g. 'set volume to 50', 'make it 30 percent', 'volume at 80')",
                    ["AdjustVolume"] = "Increase or decrease system volume without a specific target level (e.g. 'turn it up', 'louder', 'volume down', 'a bit quieter')",
                }),

            // Speculative: text candidates for utterances that carry a text payload.
            ["text"] = new JevQuestionDto(
                "choice",
                "Assume the user wants some text typed or used. `candidates` holds possible payloads extracted from the utterance. Which candidate is the intended payload, with no command words included?",
                BuildTextCriteria(textCandidates)),

            // Speculative: asked for every utterance so MediaControl plans are always ready.
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

            // Speculative: asked for every utterance so snap plans are always ready.
            ["snap_dir"] = new JevQuestionDto(
                "choice",
                "Assume the user wants to snap the current window. Which direction?",
                new Dictionary<string, string>
                {
                    ["Left"] = "Snap to left half of the screen",
                    ["Right"] = "Snap to right half of the screen",
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

        // state.candidates = transcript-derived text candidates (c0, c1, …)
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
        var criteria = new Dictionary<string, string>(candidates)
        {
            ["none"] = "No specific text content is needed"
        };
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
