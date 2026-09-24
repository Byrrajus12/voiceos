using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Browser;

public sealed class TypeSafeCommandRouter(IJevGateway gateway, double confidenceThreshold = .45) : ICommandRouter
{
    public async ValueTask<CommandRouteDecision> RouteAsync(
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var criteria = new Dictionary<string, string>
        {
            ["DIRECT_CAPABILITY"] = "The entire utterance is fully handled by one or more currently offered native VoiceOS capabilities: app/window management, media controls, or system volume.",
            ["COMPUTER_USE"] = "The utterance requires interacting within web content or a website, including searching, selecting results, filling ordinary forms, or playing a specific item. An explicitly named web service or tab is a browser scope.",
            ["NATIVE_INTERACTION"] = "The utterance requires interacting with controls inside a native desktop application, such as clicking a VS Code pane or choosing an app menu. Do not use for offered window management or media controls.",
            ["TEXT_TRANSFORM"] = "The utterance asks to rewrite, transform, or generate text rather than literally dictate it or operate a website.",
            ["CLARIFY"] = "The intent is materially ambiguous or cannot safely be assigned to another route."
        };
        var answers = await gateway.AskAsync(
            new { utterance = transcript },
            new Dictionary<string, JevQuestionDto>
            {
                ["route"] = new("choice",
                    "Route the whole utterance before anything executes. DIRECT_CAPABILITY is valid only when native capabilities cover every requested action; never execute merely a native prefix of a larger web task.",
                    criteria),
                ["intent_completeness"] = new("choice",
                    "Independently decide whether this transcript expresses an action the user wants VoiceOS to perform now. Do not supply an implied verb for a standalone name or entity. Judge only the words actually transcribed; there is no prior context available to complete an unfinished request.",
                    new Dictionary<string, string>
                    {
                        ["Actionable"] = "The utterance explicitly requests an operation, including an imperative, a media transport command, or a clear action with an object.",
                        ["BareEntity"] = "A standalone person, artist, song, service, app, website, topic, or other entity without a requested operation; or speech with no complete actionable intent."
                    }),
                ["media_request_kind"] = new("choice",
                    "Independently classify media meaning in the whole utterance. A current-media reference controls playback already in progress or selected; a new named artist, song, video, or other concrete content target requires content selection even if the verb is play. Do not infer a new target from 'the song', 'this', or similar current-media references.",
                    new Dictionary<string, string>
                    {
                        ["Transport"] = "Control the current media transport: play/resume, pause/stop, skip/next, or previous, with no new content target.",
                        ["ContentSelection"] = "Select or start a new concrete artist, song, video, or other media item, whether or not a service is named.",
                        ["None"] = "No media transport or new-media selection request."
                    }),
                ["media_op"] = new("choice",
                    "If media_request_kind is Transport, select the requested transport operation. This answer is ignored otherwise.",
                    new Dictionary<string, string>
                    {
                        ["Play"] = "Start or resume the current media.",
                        ["Pause"] = "Pause or stop the current media.",
                        ["Next"] = "Skip to the next item.",
                        ["Previous"] = "Go to the previous item."
                    })
            }, cancellationToken).ConfigureAwait(false);

        var mediaKind = answers.TryGetValue("media_request_kind", out var mediaAnswer)
            && mediaAnswer.QuestionType == "choice" && mediaAnswer.Confidence >= confidenceThreshold
            ? mediaAnswer.SelectedChoice switch
            {
                "Transport" => MediaRequestKind.Transport,
                "ContentSelection" => MediaRequestKind.ContentSelection,
                "None" => MediaRequestKind.None,
                _ => MediaRequestKind.Uncertain
            }
            : MediaRequestKind.Uncertain;
        if (!answers.TryGetValue("intent_completeness", out var intentAnswer)
            || intentAnswer.QuestionType != "choice"
            || intentAnswer.Confidence < confidenceThreshold
            || intentAnswer.SelectedChoice != "Actionable")
            return new(CommandRoute.Clarify, intentAnswer?.Confidence ?? 0,
                "No complete actionable intent was established from the transcript.",
                RoutingReason.IncompleteIntent, MediaRequestKind: mediaKind);
        if (mediaKind == MediaRequestKind.ContentSelection)
            return new(CommandRoute.ComputerUse, mediaAnswer!.Confidence,
                "A new media content target requires selection.", RoutingReason.NewContentTarget,
                MediaRequestKind: mediaKind);
        if (mediaKind == MediaRequestKind.Transport)
        {
            if (answers.TryGetValue("media_op", out var operationAnswer)
                && operationAnswer.QuestionType == "choice"
                && operationAnswer.Confidence >= confidenceThreshold
                && Enum.TryParse<MediaOperation>(operationAnswer.SelectedChoice, out var operation)
                && operation is not MediaOperation.Toggle)
                return new(CommandRoute.DirectCapability, Math.Min(mediaAnswer!.Confidence, operationAnswer.Confidence),
                    "Current-media transport.", RoutingReason.MediaTransport, operation, mediaKind);
            return new(CommandRoute.Clarify, mediaAnswer!.Confidence,
                "The current-media operation is uncertain.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind);
        }
        if (!answers.TryGetValue("route", out var answer) || answer.QuestionType != "choice" || answer.Confidence < confidenceThreshold)
            return new(CommandRoute.Clarify, answer?.Confidence ?? 0, "Routing confidence was too low.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind);
        return answer.SelectedChoice switch
        {
            "DIRECT_CAPABILITY" => new(CommandRoute.DirectCapability, answer.Confidence, MediaRequestKind: mediaKind),
            "COMPUTER_USE" => new(CommandRoute.ComputerUse, answer.Confidence, MediaRequestKind: mediaKind),
            "NATIVE_INTERACTION" => new(CommandRoute.NativeInteraction, answer.Confidence, MediaRequestKind: mediaKind),
            "TEXT_TRANSFORM" => new(CommandRoute.TextTransform, answer.Confidence, MediaRequestKind: mediaKind),
            _ => new(CommandRoute.Clarify, answer.Confidence, "The command needs clarification.", RoutingReason.AmbiguousIntent,
                MediaRequestKind: mediaKind)
        };
    }
}
