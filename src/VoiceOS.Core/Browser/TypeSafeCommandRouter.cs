using VoiceOS.Core.Decision;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VoiceOS.Core.Browser;

public sealed class TypeSafeCommandRouter(IJevGateway gateway, double confidenceThreshold = .45,
    ILogger<TypeSafeCommandRouter>? logger = null)
    : ICommandRouter, IContextualScopeDecisionSource
{
    public async ValueTask<CommandRouteDecision> RouteAsync(
        string transcript,
        CancellationToken cancellationToken = default)
        => await RouteAsync(transcript, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CommandRouteDecision> RouteAsync(
        string transcript, RecentTaskFrame? recentTask,
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
            new { utterance = transcript, recentTask = recentTask is null ? null : new {
                recentTask.SemanticGoal, recentTask.Completion, recentTask.LastUsed } },
            new Dictionary<string, JevQuestionDto>
            {
                ["route"] = new("choice",
                    "Route the whole utterance before anything executes. DIRECT_CAPABILITY is valid only when native capabilities cover every requested action; never execute merely a native prefix of a larger web task.",
                    criteria),
                ["intent_completeness"] = new("choice",
                    "Independently decide whether this transcript expresses an action the user wants VoiceOS to perform now. Do not supply an implied verb for a standalone name or entity. A supplied recent task may resolve references, but cannot turn a bare entity into a command.",
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
                    }),
                ["destination"] = new("choice",
                    "Select an explicitly named service or product destination from this registry, independently of app/web preference and tab placement. Choose None for an implicit or unlisted destination. A literal URL is extracted separately; never generate a URL.",
                    ServiceResolver.DestinationChoices),
                ["tab_disposition"] = new("choice",
                    "Independently classify explicit browser surface intent; do not match or select an actual tab. CurrentTab means the user identifies the currently active browser surface. NewTab means the user explicitly requests a new browser tab or surface. ExistingNamedTab means the user identifies a particular existing browser tab or surface by descriptive identity, title, topic, service, or name, even when that name is not a registered service. A request to open or switch to a particular named tab is ExistingNamedTab, not CurrentTab. Unspecified means no explicit browser surface disposition. Content actions on the current visible surface without explicit surface wording are handled by context_dependency.",
                    new Dictionary<string, string>
                    {
                        ["Unspecified"] = "No explicit browser surface disposition is expressed.",
                        ["CurrentTab"] = "The user identifies the currently active browser page, tab, screen, or surface.",
                        ["NewTab"] = "The user explicitly requests a new browser tab or surface.",
                        ["ExistingNamedTab"] = "The user identifies a particular existing browser tab or surface by its descriptive identity, title, topic, service, or name."
                    }),
                ["surface_preference"] = new("choice", "Classify only an explicit user preference for native application versus browser or web. A product name alone is not a preference.",
                    new Dictionary<string, string> { ["Unspecified"] = "No explicit surface type.", ["Native"] = "Explicit native app or desktop application.", ["Browser"] = "Explicit browser, web, website, or tab." }),
                ["end_state"] = new("choice", "Choose the user's desired end state for the WHOLE request. SurfaceReady only if focusing, opening, or creating the surface is the entire goal. A requested action after surface selection requires its own end state.",
                    Enum.GetNames<SemanticEndState>().ToDictionary(x => x, x => x switch {
                        "SurfaceReady" => "The requested surface is open or focused; no further content action was requested.",
                        "ResultsVisible" => "Requested results are visibly presented.",
                        "ResourceLocated" => "A requested resource is identified without requiring it to be opened.",
                        "ResourceOpened" => "The requested resource or page is open.",
                        "ContentActive" => "Requested content is playing or otherwise active.",
                        "StateChanged" => "A requested setting, value, or UI state has changed.",
                        "OtherBoundedGoal" => "Another observable bounded end state is requested.",
                        _ => "The end state cannot be determined."
                    })),
                ["goal_shape"] = new("choice", "Independently decide whether acquiring or focusing a surface is the ENTIRE request, or whether any requested interaction remains after the surface is ready. Judge the whole user goal, not the surface wording or desired end-state head.",
                    new Dictionary<string, string> { ["SurfaceOnly"] = "Opening, creating, selecting, or focusing the surface fully satisfies the request; no other action remains.",
                        ["ActionOnSurface"] = "A content, control, or state action remains after surface preparation, including multi-step tasks.",
                        ["Uncertain"] = "Cannot safely tell whether another action remains." }),
                ["task_relation"] = new("choice", "Does this utterance semantically continue or correct the supplied recent task? A fresh independent request is NewTask. Manual focus changes alone do not end continuity.",
                    new Dictionary<string, string> { ["NewTask"] = "Independent task or explicit new destination.",
                        ["ContinueRecent"] = "Recent task is useful context or a preferred surface, but this request can still be executed independently if it is stale.",
                        ["RequiresRecent"] = "The requested object or change cannot be identified without the recent task; a stale frame requires clarification.",
                        ["Uncertain"] = "Relation cannot be established safely." }),
                ["context_dependency"] = new("choice",
                    "Does this goal depend on information, controls, ordering, or items visible on the current surface right now? Classify current-surface dependency only; do not choose a tab, app, or execution surface. A visible target can require the current surface even when the utterance does not explicitly name the page or tab. TaskRelation separately classifies continuity with earlier VoiceOS work. Explicit tab disposition and destination are decided by separate heads.",
                    new Dictionary<string, string>
                    {
                        ["SelfContained"] = "The utterance contains enough information to understand the goal without relying on the currently visible surface, including a specific search or destination.",
                        ["RequiresCurrentSurface"] = "The target or action depends on information, controls, ordering, or items visible on the current surface right now.",
                        ["Uncertain"] = "Insufficient confidence to classify current-surface dependency."
                    })
            }, cancellationToken).ConfigureAwait(false);

        var literalUrl = ExtractLiteralUrl(transcript);
        var destinationAnswer = answers.TryGetValue("destination", out var da) && da.QuestionType == "choice"
            && da.Confidence >= confidenceThreshold ? da.SelectedChoice : null;
        var service = ServiceResolver.Resolve(destinationAnswer);
        var disposition = answers.TryGetValue("tab_disposition", out var td) && td.QuestionType == "choice"
            && td.Confidence >= confidenceThreshold && Enum.TryParse<TabDisposition>(td.SelectedChoice, out var parsed)
            ? parsed : TabDisposition.Unspecified;
        var namedTab = disposition == TabDisposition.ExistingNamedTab ? service?.CanonicalName : null;
        var destinationKind = literalUrl is not null ? SemanticDestinationKind.ExplicitUrl
            : disposition == TabDisposition.ExistingNamedTab ? SemanticDestinationKind.NamedTab
            : service is not null ? SemanticDestinationKind.KnownService : SemanticDestinationKind.None;
        var destinationName = destinationKind == SemanticDestinationKind.NamedTab ? namedTab : service?.CanonicalName;
        CommandRouteDecision WithScope(CommandRouteDecision value) => value with
        {
            DestinationKind = destinationKind, DestinationName = destinationName,
            ExplicitUrl = literalUrl, TabDisposition = disposition,
            SurfacePreference = ChoiceEnum<SurfacePreference>(answers, "surface_preference", confidenceThreshold),
            EndState = ChoiceEnum<SemanticEndState>(answers, "end_state", confidenceThreshold),
            TaskRelation = ChoiceEnum<TaskRelation>(answers, "task_relation", confidenceThreshold),
            GoalShape = ChoiceEnum<GoalShape>(answers, "goal_shape", confidenceThreshold),
            ContextDependency = ChoiceEnum<ContextDependency>(answers, "context_dependency", confidenceThreshold)
        };

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
            return WithScope(new(CommandRoute.Clarify, intentAnswer?.Confidence ?? 0,
                "No complete actionable intent was established from the transcript.",
                RoutingReason.IncompleteIntent, MediaRequestKind: mediaKind));
        if (mediaKind == MediaRequestKind.ContentSelection)
            return WithScope(new(CommandRoute.ComputerUse, mediaAnswer!.Confidence,
                "A new media content target requires selection.", RoutingReason.NewContentTarget,
                MediaRequestKind: mediaKind));
        if (mediaKind == MediaRequestKind.Transport)
        {
            if (destinationKind != SemanticDestinationKind.None || disposition != TabDisposition.Unspecified)
                return WithScope(new(CommandRoute.ComputerUse, mediaAnswer!.Confidence,
                    "Transport explicitly scoped to a browser surface.", MediaRequestKind: mediaKind));
            if (answers.TryGetValue("media_op", out var operationAnswer)
                && operationAnswer.QuestionType == "choice"
                && operationAnswer.Confidence >= confidenceThreshold
                && Enum.TryParse<MediaOperation>(operationAnswer.SelectedChoice, out var operation)
                && operation is not MediaOperation.Toggle)
                return WithScope(new(CommandRoute.DirectCapability, Math.Min(mediaAnswer!.Confidence, operationAnswer.Confidence),
                    "Current-media transport.", RoutingReason.MediaTransport, operation, mediaKind));
            return WithScope(new(CommandRoute.Clarify, mediaAnswer!.Confidence,
                "The current-media operation is uncertain.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind));
        }
        if (!answers.TryGetValue("route", out var answer) || answer.QuestionType != "choice" || answer.Confidence < confidenceThreshold)
            return WithScope(new(CommandRoute.Clarify, answer?.Confidence ?? 0, "Routing confidence was too low.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind));
        return WithScope(answer.SelectedChoice switch
        {
            "DIRECT_CAPABILITY" => new(CommandRoute.DirectCapability, answer.Confidence, MediaRequestKind: mediaKind),
            "COMPUTER_USE" => new(CommandRoute.ComputerUse, answer.Confidence, MediaRequestKind: mediaKind),
            "NATIVE_INTERACTION" => new(CommandRoute.NativeInteraction, answer.Confidence, MediaRequestKind: mediaKind),
            "TEXT_TRANSFORM" => new(CommandRoute.TextTransform, answer.Confidence, MediaRequestKind: mediaKind),
            _ => new(CommandRoute.Clarify, answer.Confidence, "The command needs clarification.", RoutingReason.AmbiguousIntent,
                MediaRequestKind: mediaKind)
        });
    }

    public async ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
        ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered,
        CancellationToken cancellationToken = default)
    {
        var criteria = offered.Distinct().ToDictionary(x => x.ToString(), x => x switch
        {
            ContextualSurface.ActiveBrowserTab => "Use only when the user semantically addresses this current page or strong task context makes it the intended surface. Mere technical ability to perform the action is insufficient.",
            ContextualSurface.RecentOwnedBrowserTab => "Reuse this previously owned VoiceOS task tab only when its metadata makes the action belong there.",
            ContextualSurface.NewBrowserTaskTab => "Preserve unrelated user work and start a separate VoiceOS task tab for a fresh browser task.",
            ContextualSurface.ForegroundNativeWindow => "The complete action concerns controls inside the foreground native app; representation only.",
            _ => "Context does not safely identify an execution surface."
        });
        var active = context.BrowserTabs.FirstOrDefault(t => t.Active);
        var owned = context.BrowserTabs.Where(t => t.Provenance == BrowserTabProvenance.VoiceOs
            && t.LastUsedSequence is not null).OrderByDescending(t => t.LastUsedSequence).FirstOrDefault();
        var result = await gateway.AskAsync(new
        {
            utterance, intent = intent.Route.ToString(), intent.MediaRequestKind,
            foreground = context.ForegroundWindow is { } w ? new { w.ProcessName, w.Title } : null,
            activeTab = active is null ? null : new { origin = active.Origin?.AbsoluteUri, active.Title },
            recentOwnedTab = owned is null ? null : new { origin = owned.Origin?.AbsoluteUri, owned.Title }
        }, new Dictionary<string, JevQuestionDto>
        {
            ["surface"] = new("choice", "Choose only one offered execution surface from metadata. Do not infer an action for a bare entity. No DOM or page actions are available here.", criteria)
        }, cancellationToken).ConfigureAwait(false);
        return result.TryGetValue("surface", out var answer) && answer.QuestionType == "choice"
            && answer.Confidence >= confidenceThreshold
            && Enum.TryParse<ContextualSurface>(answer.SelectedChoice, out var selected) && offered.Contains(selected)
            ? selected : ContextualSurface.Clarify;
    }

    public async ValueTask<int?> SelectNamedTabAsync(string utterance,
        IReadOnlyList<BrowserTabInfo> tabs, CancellationToken cancellationToken = default)
    {
        var offered = tabs.Where(t => t.Origin is not null).Take(128).ToArray();
        if (offered.Length == 0)
        {
            logger?.LogInformation("Named tab reference={Reference} candidate_count=0 outcome=clarify reason=no_candidates", utterance);
            return null;
        }
        string? KnownService(BrowserTabInfo tab) => ServiceResolver.DestinationChoices.Keys
            .Select(ServiceResolver.Resolve)
            .FirstOrDefault(s => s is not null && Uri.Compare(tab.Origin!, s.WebOrigin,
                UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            ?.CanonicalName;
        var ids = offered.ToDictionary(t => $"tab_{t.TabId}", t =>
        {
            return $"title={t.Title}; origin={t.Origin}; host={t.Origin?.Host}; service={KnownService(t) ?? "unknown"}";
        });
        ids["NONE"] = "No offered tab is a sufficiently plausible match.";
        ids["AMBIGUOUS"] = "Multiple offered tabs plausibly match the reference.";
        logger?.LogInformation("Named tab reference={Reference} candidate_count={Count}", utterance, offered.Length);
        foreach (var tab in offered)
        {
            var candidateId = $"tab_{tab.TabId}";
            logger?.LogInformation("Named tab candidate_id={CandidateId} title={Title} hostname={Hostname} origin={Origin} service={Service}",
                candidateId, tab.Title, tab.Origin?.Host, tab.Origin, KnownService(tab));
        }
        var answers = await gateway.AskAsync(new { utterance }, new Dictionary<string, JevQuestionDto>
        {
            ["tab"] = new("choice",
                "Which offered existing browser tab does the user's reference identify? Choose one offered candidate ID only when exactly one candidate is the clear semantic match. Choose AMBIGUOUS when multiple offered candidates plausibly satisfy the reference. Choose NONE when no offered candidate is sufficiently plausible. Use title, hostname, origin, and known service identity. Do not invent a tab, URL, service, or candidate.", ids)
        }, cancellationToken).ConfigureAwait(false);
        var tabThreshold = Math.Max(confidenceThreshold, .7);
        answers.TryGetValue("tab", out var answer);
        logger?.LogInformation("Named tab selected_semantic_value={Selection} confidence={Confidence:F2} confidence_floor={Floor:F2} confidence_floor_passed={Passed}",
            answer?.SelectedChoice, answer?.Confidence, tabThreshold,
            answer?.QuestionType == "choice" && answer.Confidence >= tabThreshold);
        int? Reject(string reason)
        {
            logger?.LogInformation("Named tab outcome=clarify reason={Reason}", reason);
            return null;
        }
        if (answer?.QuestionType != "choice" || answer.SelectedChoice is null)
            return Reject("missing_or_invalid_choice");
        if (answer.Confidence < tabThreshold) return Reject("choice_confidence_below_floor");
        if (answer.SelectedChoice == "NONE") return Reject("no_plausible_match");
        if (answer.SelectedChoice == "AMBIGUOUS") return Reject("multiple_plausible_matches");
        if (!ids.ContainsKey(answer.SelectedChoice)) return Reject("candidate_not_offered");
        if (!int.TryParse(answer.SelectedChoice.AsSpan(4), out var id)) return Reject("invalid_candidate_id");
        var selected = offered.SingleOrDefault(t => t.TabId == id);
        if (selected is null) return Reject("candidate_missing");
        var duplicates = offered.Count(t => string.Equals(t.Title, selected.Title,
            StringComparison.OrdinalIgnoreCase) && Equals(t.Origin, selected.Origin));
        if (duplicates != 1) return Reject("duplicate_title_and_origin");
        logger?.LogInformation("Named tab outcome=selected candidate_id={CandidateId}", answer.SelectedChoice);
        return id;
    }

    public async ValueTask<string?> SelectInstalledAppAsync(string utterance,
        IReadOnlyList<VoiceOS.Core.Candidates.AppCandidate> apps,
        CancellationToken cancellationToken = default)
    {
        var offered = apps.Take(128).ToArray();
        if (offered.Length == 0) return null;
        var ids = offered.ToDictionary(x => x.Id, x => x.DisplayName, StringComparer.Ordinal);
        ids["none"] = "No installed app clearly names the requested product.";
        var answers = await gateway.AskAsync(new { utterance }, new Dictionary<string, JevQuestionDto>
        {
            ["app"] = new("choice", "Choose an installed native app only when the user clearly names that product as the intended task destination. Use only offered app IDs. Incidental app mentions and foreground context are not sufficient. Choose none if uncertain.", ids)
        }, cancellationToken).ConfigureAwait(false);
        return answers.TryGetValue("app", out var answer) && answer.QuestionType == "choice"
            && answer.Confidence >= confidenceThreshold && answer.SelectedChoice is { } id
            && id != "none" && offered.Any(x => x.Id == id) ? id : null;
    }

    private static Uri? ExtractLiteralUrl(string transcript)
    {
        foreach (Match match in Regex.Matches(transcript, @"https?://[^\s<>\""']+", RegexOptions.IgnoreCase))
            if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ')', ']'), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                return uri;
        return null;
    }

    private static T ChoiceEnum<T>(IReadOnlyDictionary<string, JevAnswer> answers, string head,
        double threshold) where T : struct, Enum
        => answers.TryGetValue(head, out var answer) && answer.QuestionType == "choice"
            && answer.Confidence >= threshold && Enum.TryParse<T>(answer.SelectedChoice, out var value)
            ? value : default;
}
