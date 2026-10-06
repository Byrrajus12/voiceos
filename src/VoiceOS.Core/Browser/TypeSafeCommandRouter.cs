using VoiceOS.Core.Interaction;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Activation;
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
        => await RouteAsync(transcript, recentTask, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<CommandRouteDecision> RouteAsync(
        string transcript, RecentTaskFrame? recentTask, FrontDoorContext? context,
        CancellationToken cancellationToken = default, bool earlierReferentsAvailable = false)
        => RouteInvariants.Apply(await RouteCoreAsync(transcript, recentTask, context, cancellationToken,
            earlierReferentsAvailable).ConfigureAwait(false));

    private async ValueTask<CommandRouteDecision> RouteCoreAsync(
        string transcript, RecentTaskFrame? recentTask, FrontDoorContext? context,
        CancellationToken cancellationToken, bool earlierReferentsAvailable)
    {
        var criteria = new Dictionary<string, string>
        {
            ["DIRECT_CAPABILITY"] = "The entire utterance is fully handled by one or more currently offered native VoiceOS capabilities: app/window management, media controls, or system volume.",
            ["COMPUTER_USE"] = "The utterance requires interacting within web content or a website, including searching, selecting results, filling ordinary forms, or playing a specific item. An explicitly named web service or tab is a browser scope.",
            ["NATIVE_INTERACTION"] = "The utterance requires interacting with controls inside a native desktop application, such as clicking a VS Code pane or choosing an app menu. Do not use for offered window management or media controls.",
            ["TEXT_TRANSFORM"] = "The utterance asks to rewrite, transform, or generate text rather than literally dictate it or operate a website.",
            ["CLARIFY"] = "The intent is materially ambiguous or cannot safely be assigned to another route."
        };
        object state = context is null
            ? new { utterance = transcript, recentTask = recentTask is null ? null : new {
                recentTask.SemanticGoal, recentTask.Completion, recentTask.LastUsed } }
            : new { utterance = transcript, context = context.ToJevState() };
        string Grounded(string text) => context is null ? "" : " " + text;
        var heads = new Dictionary<string, JevQuestionDto>
            {
                ["route"] = new("choice",
                    "Route the whole utterance before anything executes. DIRECT_CAPABILITY is valid only when native capabilities cover every requested action; never execute merely a native prefix of a larger web task." + Grounded("Use context.named_matches and context.foreground: a named installed app or open window that is not a web service, operated with window/app verbs, is DIRECT_CAPABILITY. When installed-app and web-service matches coexist, retain the ambiguity rather than assuming either representation."),
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
                ["return_target"] = new("choice",
                    "Independently classify what a request to go back or forward, return, or go to the previous or next thing moves through. Judge the whole utterance, not the verb alone.",
                    new Dictionary<string, string>
                    {
                        ["None"] = "No going back, returning, or previous-item request.",
                        ["MediaPlayback"] = "The previous track, song, episode, video, or other item in media playback.",
                        ["NavigationHistory"] = "The previous or next page, screen, or location in browser or app navigation history.",
                        ["Uncertain"] = "Going back is requested, but whether media playback or navigation is meant cannot be established."
                    }),
                ["page_operation"] = new("choice",
                    "Is the ENTIRE request exactly one of these operations on the page already in view? Back and Forward are one step through browser history ('go back', 'go forward', 'go back one more time'). ScrollDown and ScrollUp scroll the page once. A request that also names a target, destination, query, or a second action is None.",
                    new Dictionary<string, string>
                    {
                        ["None"] = "Anything else.",
                        ["Back"] = "Exactly one step back in history.",
                        ["Forward"] = "Exactly one step forward in history.",
                        ["ScrollDown"] = "Exactly one scroll down the current page.",
                        ["ScrollUp"] = "Exactly one scroll up the current page."
                    }),
                ["requested_entity"] = new("choice",
                    "Independently identify what the user wants opened, focused, or used, regardless of surface preference, tab wording, or whether it is installed or listed anywhere.",
                    new Dictionary<string, string>
                    {
                        ["None"] = "No particular app, service, website, or product is requested, such as current-window, media, volume, or current-page actions.",
                        ["BrowserItself"] = "Only a generic web browser itself, a browser window, or a blank or new browser tab.",
                        ["NamedEntity"] = "A specific named app, service, website, or product other than a generic web browser, whether or not it is registered or installed."
                    }),
                ["destination"] = new("choice",
                    "Select an explicitly named service or product destination from this registry, independently of app/web preference and tab placement. Choose None for an implicit or unlisted destination. A literal URL is extracted separately; never generate a URL.",
                    ServiceResolver.DestinationChoices),
                ["tab_disposition"] = new("choice",
                    "Independently classify explicit browser surface intent; do not match or select an actual tab. CurrentTab means the user identifies the currently active browser surface. NewTab means the user explicitly requests a new browser tab or surface. ExistingNamedTab means the user refers to a particular already-open browser tab or surface as such, identifying it by descriptive identity, title, service, or name, even when that name is not a registered service. Content, an item, or a link to act on within a page is not a tab reference. A request to open or switch to a particular named tab is ExistingNamedTab, not CurrentTab. Naming a service, site, or place to go to, open, or use is a destination, classified by the separate destination head, and is not ExistingNamedTab unless the user also refers to an existing tab. Unspecified means no explicit browser surface disposition. Content actions on the current visible surface without explicit surface wording are handled by context_dependency." + Grounded("context.browser.matching_tabs lists open tabs that plausibly match the wording; a reference matching none of them, or naming an item on the visible page, is not ExistingNamedTab."),
                    new Dictionary<string, string>
                    {
                        ["Unspecified"] = "No explicit browser surface disposition is expressed.",
                        ["CurrentTab"] = "The user identifies the currently active browser page, tab, screen, or surface.",
                        ["NewTab"] = "The user explicitly requests a new browser tab or surface.",
                        ["ExistingNamedTab"] = "The user refers to a particular already-open browser tab or surface by its descriptive identity, title, service, or name; a destination to go to is not a tab reference, nor is content to act on within a page."
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
                ["task_relation"] = new("choice", "Does this utterance semantically continue or correct the supplied recent task? A fresh independent request is NewTask. Manual focus changes alone do not end continuity." + Grounded("context.recent_task provides validated recent task metadata, including still_active_tab and seconds_ago. Use these facts to judge continuity; an absent frame cannot establish a required prior task."),
                    new Dictionary<string, string> { ["NewTask"] = "Independent task or explicit new destination.",
                        ["ContinueRecent"] = "Recent task is useful context or a preferred surface, but this request can still be executed independently if it is stale.",
                        ["RequiresRecent"] = "The requested object or change cannot be identified without the recent task; a stale frame requires clarification.",
                        ["Uncertain"] = "Relation cannot be established safely." }),
                ["context_dependency"] = new("choice",
                    "Classify whether the complete request plausibly addresses content or controls on the currently visible surface; do not choose a tab, app, or execution surface. TaskRelation separately classifies continuity with earlier VoiceOS work. A target described by a property or relative identity can require this page for observation even though the matching item has not been identified. Decide only where the missing reference comes from, not which target matches or whether the task will succeed. A fully specified fresh lookup or destination is SelfContained. Task independence and a surface-opening outcome do not establish target independence." + Grounded("context.foreground and context.browser.active_tab describe the visible surface. If that page plausibly supplies a described target, choose RequiresCurrentSurface. You do not need DOM contents or a proven match to establish dependency for observation. An unrelated page cannot supply a self-contained task's target."),
                    new Dictionary<string, string>
                    {
                        ["SelfContained"] = "The utterance contains enough information to understand the goal without relying on the currently visible surface, including a specific search or destination.",
                        ["RequiresCurrentSurface"] = "The complete action addresses a target supplied or disambiguated by the visible surface; inspect that surface to bind the target later.",
                        ["Uncertain"] = "Insufficient confidence to classify current-surface dependency."
                    })
            };
        // Only asked when VoiceOS actually holds earlier pages or items; it rides in the same request.
        if (earlierReferentsAvailable)
            heads["earlier_reference"] = new("choice",
                "Independently decide whether the request can only be understood by reference to something opened or used earlier in this session: it uses a pronoun, again, the other one, the previous one, the first one, that page, or an incomplete description of something already opened. A request that fully names or describes its own target is NotEarlier even if that target happened to be opened before, and so is a request about what is on screen right now. Do not choose which page.",
                new Dictionary<string, string>
                {
                    ["Earlier"] = "The request depends on earlier work to identify its target and does not name a complete target itself.",
                    ["NotEarlier"] = "The request names something new, or addresses the visible surface, or does not refer back."
                });
        var answers = await gateway.AskAsync(state, heads, cancellationToken).ConfigureAwait(false);

        foreach (var head in answers)
            JevDiagnostics.Log(logger, "Command route head", head.Key, head.Value);
        var literalUrl = ExtractLiteralUrl(transcript);
        var destinationAnswer = answers.TryGetValue("destination", out var da) && da.QuestionType == "choice"
            && da.Confidence >= confidenceThreshold ? da.SelectedChoice : null;
        var suggested = ServiceResolver.Resolve(destinationAnswer);
        // A destination is authoritative only when the user's own words name it (or a literal URL does).
        var service = DestinationGrounding.Names(transcript, suggested) ? suggested : null;
        if (suggested is not null && service is null)
            logger?.LogInformation("Destination suggested={Suggested} grounded=false: not named by the user, ignored", suggested.CanonicalName);
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
            RawAnswers = answers,
            IntentActionable = answers.TryGetValue("intent_completeness", out var complete)
                && complete.QuestionType == "choice" && complete.Confidence >= confidenceThreshold
                && complete.SelectedChoice == "Actionable",
            CoarseBrowserCandidate = answers.TryGetValue("route", out var coarse)
                && coarse.QuestionType == "choice" && coarse.SelectedChoice == "COMPUTER_USE",
            CoarseNonBrowserCandidate = answers.TryGetValue("route", out var alternative)
                && alternative.QuestionType == "choice"
                && alternative.SelectedChoice is "DIRECT_CAPABILITY" or "NATIVE_INTERACTION" or "TEXT_TRANSFORM",
            ReturnTarget = ChoiceEnum<ReturnTarget>(answers, "return_target", confidenceThreshold),
            PageOperation = ChoiceEnum<PageOperation>(answers, "page_operation", confidenceThreshold),
            ReferencesEarlier = answers.TryGetValue("earlier_reference", out var earlier)
                && earlier.QuestionType == "choice" && earlier.Confidence >= Math.Max(confidenceThreshold, .6)
                && earlier.SelectedChoice == "Earlier",
            TaskRelationEstablished = answers.TryGetValue("task_relation", out var relation)
                && relation.QuestionType == "choice" && relation.Confidence >= confidenceThreshold
                && Enum.TryParse<TaskRelation>(relation.SelectedChoice, out var taskRelation)
                && Enum.IsDefined(taskRelation),
            DestinationKind = destinationKind, DestinationName = destinationName,
            SuggestedDestination = service is null ? suggested?.CanonicalName : null,
            ExplicitUrl = literalUrl, TabDisposition = disposition,
            SurfacePreference = ChoiceEnum<SurfacePreference>(answers, "surface_preference", confidenceThreshold),
            EndState = ChoiceEnum<SemanticEndState>(answers, "end_state", confidenceThreshold),
            TaskRelation = ChoiceEnum<TaskRelation>(answers, "task_relation", confidenceThreshold),
            GoalShape = ChoiceEnum<GoalShape>(answers, "goal_shape", confidenceThreshold),
            ContextDependency = ChoiceEnum<ContextDependency>(answers, "context_dependency", confidenceThreshold),
            RequestedEntity = ChoiceEnum<RequestedEntityKind>(answers, "requested_entity", confidenceThreshold)
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
        var returnTarget = ChoiceEnum<ReturnTarget>(answers, "return_target", confidenceThreshold);
        // Going back through navigation history is never a native capability; it acts on the
        // current browser surface, whose availability the scope resolver decides.
        CommandRouteDecision NavigationBack(double confidence) => WithScope(new(CommandRoute.ComputerUse,
            confidence, "Navigation back on the current surface.", MediaRequestKind: MediaRequestKind.None))
            with { ContextDependency = ContextDependency.RequiresCurrentSurface };
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
            {
                // Previous collides with navigation Back, so it needs affirmative media semantics;
                // the media_op prediction alone never emits a media key.
                // An unresolved return is preserved for the scope resolver, which owns current-surface context.
                if (operation == MediaOperation.Previous && returnTarget != ReturnTarget.MediaPlayback)
                    return returnTarget == ReturnTarget.NavigationHistory
                        ? NavigationBack(mediaAnswer!.Confidence)
                        : WithScope(new(CommandRoute.Clarify, mediaAnswer!.Confidence,
                            "Going back could mean media playback or page navigation.",
                            returnTarget == ReturnTarget.Uncertain ? RoutingReason.UnresolvedReturn : RoutingReason.AmbiguousIntent,
                            MediaRequestKind: mediaKind));
                return WithScope(new(CommandRoute.DirectCapability, Math.Min(mediaAnswer!.Confidence, operationAnswer.Confidence),
                    "Current-media transport.", RoutingReason.MediaTransport, operation, mediaKind));
            }
            return WithScope(new(CommandRoute.Clarify, mediaAnswer!.Confidence,
                "The current-media operation is uncertain.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind));
        }
        if (!answers.TryGetValue("route", out var answer) || answer.QuestionType != "choice" || answer.Confidence < confidenceThreshold)
            return WithScope(new(CommandRoute.Clarify, answer?.Confidence ?? 0, "Routing confidence was too low.", RoutingReason.LowConfidence,
                MediaRequestKind: mediaKind));
        if (answer.SelectedChoice == "DIRECT_CAPABILITY")
        {
            if (returnTarget == ReturnTarget.NavigationHistory)
                return NavigationBack(answer.Confidence);
            // An explicit web preference for a named entity is a browser destination; no native
            // capability (including opening the browser app itself) satisfies it.
            var scoped = WithScope(new(CommandRoute.DirectCapability, answer.Confidence, MediaRequestKind: mediaKind));
            if (scoped.SurfacePreference == SurfacePreference.Browser && scoped.RequestsNamedEntity
                && scoped.GoalShape != GoalShape.ActionOnSurface)
                return scoped with { Route = CommandRoute.ComputerUse, Detail = "Explicit web destination." };
        }
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
            ContextualSurface.ActiveBrowserTab => "Use when the complete request plausibly addresses content or controls on the visible page. Select the page for observation; you do not need to identify or prove the requested target from metadata. The browser executor binds the target and proves success later. Mere technical ability to perform an unrelated fresh task is insufficient.",
            ContextualSurface.RecentOwnedBrowserTab => "Reuse this previously owned VoiceOS task tab only when its metadata makes the action belong there.",
            ContextualSurface.NewBrowserTaskTab => "Start a separate task tab when the utterance supplies an independent destination or information lookup. TaskRelation NewTask alone is insufficient: a new goal can still refer to content on the visible page.",
            ContextualSurface.ForegroundNativeWindow => "The complete action concerns controls inside the foreground native app; representation only.",
            ContextualSurface.DirectCapability => "Run the native action now: " + context.SafeDirectOffer?.Summary,
            _ => "No offered surface is safe even for observation: the goal is incomplete, unsupported, conflicting, or depends on an unresolved reference to another surface. Uncertainty about which item on the visible page matches does not by itself require this choice."
        });
        var active = context.BrowserTabs.FirstOrDefault(t => t.Active);
        var owned = context.FrontDoor is null ? context.BrowserTabs.Where(t => t.Provenance == BrowserTabProvenance.VoiceOs
                && t.LastUsedSequence is not null).OrderByDescending(t => t.LastUsedSequence).FirstOrDefault()
            : context.RecentTask is { } frame ? context.BrowserTabs.FirstOrDefault(t => t.TabId == frame.TabId) : null;
        var semantics = new
        {
            intent.IntentActionable, contextDependency = intent.ContextDependency.ToString(),
            surfacePreference = intent.SurfacePreference.ToString(), taskRelation = intent.TaskRelation.ToString(),
            intent.TaskRelationEstablished, goalShape = intent.GoalShape.ToString(),
            endState = intent.EndState.ToString(), requestedEntity = intent.RequestedEntity.ToString(),
            returnTarget = intent.ReturnTarget.ToString()
        };
        object pickerState = context.FrontDoor is null ? new
        {
            utterance, preliminaryRoute = intent.Route.ToString(), routeReason = intent.Reason.ToString(), intent.MediaRequestKind, semantics,
            foreground = context.ForegroundWindow is { } legacyWindow ? new { legacyWindow.ProcessName, legacyWindow.Title } : null,
            activeTab = active is null ? null : new { origin = active.Origin?.AbsoluteUri, active.Title },
            recentOwnedTab = owned is null ? null : new { origin = owned.Origin?.AbsoluteUri, owned.Title }
        } : new
        {
            utterance, preliminaryRoute = intent.Route.ToString(), routeReason = intent.Reason.ToString(), intent.MediaRequestKind, semantics,
            foreground = context.ForegroundWindow is { } w ? new { w.ProcessName, w.Title } : null,
            activeTab = active is null ? null : new { origin = active.Origin?.AbsoluteUri, active.Title },
            recentOwnedTab = owned is null ? null : new { origin = owned.Origin?.AbsoluteUri, owned.Title },
            context = context.FrontDoor?.ToJevState()
        };
        var result = await gateway.AskAsync(pickerState, new Dictionary<string, JevQuestionDto>
        {
            ["surface"] = new("choice", "Choose only one offered execution surface from metadata and established semantics. A preliminary Clarify route can reflect coarse confidence uncertainty; it does not erase established semantic heads. Current-page entry is for observation, not target binding or completion. When the request describes an unnamed target relative to visible content, choose the visible page to inspect it even if context dependency is Uncertain; an unresolved target identity alone does not require Clarify. SurfaceOnly does not establish a self-contained destination. NewTask describes task independence and can still require the visible page. Self-contained fresh browser goals belong in a new task tab. Do not infer an action for a bare entity. No DOM or page actions are available here.", criteria)
        }, cancellationToken).ConfigureAwait(false);
        foreach (var head in result)
            JevDiagnostics.Log(logger, "Contextual surface", head.Key, head.Value);
        return result.TryGetValue("surface", out var answer) && answer.QuestionType == "choice"
            && answer.Confidence >= confidenceThreshold
            && Enum.TryParse<ContextualSurface>(answer.SelectedChoice, out var selected) && offered.Contains(selected)
            ? selected : ContextualSurface.Clarify;
    }

    /// <summary>
    /// Settles a named-tab claim against the real tab inventory. Selecting a tab needs a
    /// high-confidence unique match; refuting the claim (NoMatch) needs only ordinary confidence
    /// because a refuted claim never switches tabs by itself.
    /// </summary>
    public async ValueTask<NamedTabSelection> SelectNamedTabAsync(string utterance,
        IReadOnlyList<BrowserTabInfo> tabs, CancellationToken cancellationToken = default)
    {
        var offered = tabs.Where(t => t.Origin is not null).Take(128).ToArray();
        if (offered.Length == 0)
        {
            logger?.LogInformation("Named tab reference={Reference} candidate_count=0 outcome=no_match reason=no_candidates", utterance);
            return NamedTabSelection.NoMatch("no_candidates");
        }
        string? KnownService(BrowserTabInfo tab) => ServiceResolver.DestinationChoices.Keys
            .Select(ServiceResolver.Resolve)
            .FirstOrDefault(s => s is not null && Uri.Compare(tab.Origin!, s.WebOrigin,
                UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            ?.CanonicalName;
        // Recency rank 1 is the most recently used tab VoiceOS knows about.
        var recency = offered.Where(t => t.LastUsedSequence is not null)
            .OrderByDescending(t => t.LastUsedSequence)
            .Select((t, index) => (t.TabId, Rank: index + 1))
            .ToDictionary(x => x.TabId, x => x.Rank);
        string Describe(BrowserTabInfo t)
            => $"title={t.Title}; origin={t.Origin}; host={t.Origin?.Host}; service={KnownService(t) ?? "unknown"}; "
                + $"active={(t.Active ? "yes" : "no")}; opened_by={(t.Provenance == BrowserTabProvenance.VoiceOs ? "VoiceOS" : "user")}; "
                + $"recency_rank={(recency.TryGetValue(t.TabId, out var rank) ? rank.ToString() : "unknown")}";
        var ids = offered.ToDictionary(t => $"tab_{t.TabId}", Describe);
        ids["NONE"] = "No offered tab is a sufficiently plausible match; the reference is not to any open tab.";
        ids["AMBIGUOUS"] = "Multiple offered tabs plausibly match the reference.";
        logger?.LogInformation("Named tab reference={Reference} candidate_count={Count}", utterance, offered.Length);
        foreach (var tab in offered)
        {
            var candidateId = $"tab_{tab.TabId}";
            logger?.LogInformation("Named tab candidate_id={CandidateId} title={Title} hostname={Hostname} origin={Origin} service={Service} active={Active} provenance={Provenance} recency_rank={Rank}",
                candidateId, tab.Title, tab.Origin?.Host, tab.Origin, KnownService(tab), tab.Active, tab.Provenance,
                recency.TryGetValue(tab.TabId, out var r) ? r : null);
        }
        var answers = await gateway.AskAsync(new { utterance }, new Dictionary<string, JevQuestionDto>
        {
            ["tab"] = new("choice",
                "Which offered existing browser tab does the user's reference identify? Choose one offered candidate ID only when exactly one candidate is the clear semantic match. Choose AMBIGUOUS when multiple offered candidates plausibly satisfy the reference. Choose NONE when no offered candidate is sufficiently plausible, including when the reference names content, an item, or a link rather than a tab. Use title, hostname, origin, and known service identity; when several match, whether VoiceOS opened a tab and how recently it was used may distinguish them. Do not invent a tab, URL, service, or candidate.", ids)
        }, cancellationToken).ConfigureAwait(false);
        var tabThreshold = Math.Max(confidenceThreshold, .7);
        foreach (var head in answers)
            JevDiagnostics.Log(logger, "Named tab", head.Key, head.Value);
        answers.TryGetValue("tab", out var answer);
        logger?.LogInformation("Named tab selected_semantic_value={Selection} confidence={Confidence:F2} confidence_floor={Floor:F2} confidence_floor_passed={Passed}",
            answer?.SelectedChoice, answer?.Confidence, tabThreshold,
            answer?.QuestionType == "choice" && answer.Confidence >= tabThreshold);
        NamedTabSelection Outcome(NamedTabSelection selection)
        {
            logger?.LogInformation("Named tab outcome={Outcome} reason={Reason} candidate_id={CandidateId}",
                selection.Kind, selection.Reason ?? "-", selection.TabId is int id ? $"tab_{id}" : "-");
            return selection;
        }
        if (answer?.QuestionType != "choice" || answer.SelectedChoice is null)
            return Outcome(NamedTabSelection.Unavailable("missing_or_invalid_choice"));
        if (answer.Confidence < confidenceThreshold)
            return Outcome(NamedTabSelection.Unavailable("choice_confidence_below_threshold"));
        if (answer.SelectedChoice == "NONE") return Outcome(NamedTabSelection.NoMatch("no_plausible_match"));
        if (answer.SelectedChoice == "AMBIGUOUS") return Outcome(NamedTabSelection.Ambiguous("multiple_plausible_matches"));
        if (!ids.ContainsKey(answer.SelectedChoice)) return Outcome(NamedTabSelection.Unavailable("candidate_not_offered"));
        if (!int.TryParse(answer.SelectedChoice.AsSpan(4), out var tabId))
            return Outcome(NamedTabSelection.Unavailable("invalid_candidate_id"));
        var selected = offered.SingleOrDefault(t => t.TabId == tabId);
        if (selected is null) return Outcome(NamedTabSelection.Unavailable("candidate_missing"));
        // A selection that is not confidently unique is not a switch.
        if (answer.Confidence < tabThreshold)
            return Outcome(NamedTabSelection.Ambiguous("selection_confidence_below_floor"));
        var duplicates = offered.Count(t => string.Equals(t.Title, selected.Title,
            StringComparison.OrdinalIgnoreCase) && Equals(t.Origin, selected.Origin));
        if (duplicates != 1) return Outcome(NamedTabSelection.Ambiguous("duplicate_title_and_origin"));
        return Outcome(NamedTabSelection.Select(tabId));
    }

    /// <summary>
    /// Picks which validated earlier referent a spoken reference denotes. One request, two heads: the
    /// candidate and the semantic relation. Nothing outside the offered typed list can be returned.
    /// </summary>
    public async ValueTask<ReferentChoice> SelectReferentAsync(string utterance,
        IReadOnlyList<ReferentCandidate> candidates, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) return new(ReferentChoiceKind.None);
        string Describe(ReferentCandidate c)
            => $"{(c.Referent.Kind == ReferentKind.Page ? "page" : "item")}: title={c.Referent.Label}; site={c.Site}; "
                + $"in_view={(c.Current ? "yes" : "no")}; recency_rank={c.Rank}; "
                + $"same_site_set={(c.Group is null ? "no" : "yes")}; opened_by={(c.Referent.OwnedByVoiceOs ? "VoiceOS" : "user")}";
        var ids = candidates.ToDictionary(c => c.Id, Describe);
        ids["NONE"] = "No offered candidate is what the reference denotes.";
        ids["AMBIGUOUS"] = "More than one offered candidate plausibly matches the reference.";
        foreach (var c in candidates)
            logger?.LogInformation("Referent candidate id={Id} kind={Kind} site={Site} in_view={InView} rank={Rank} provenance={Provenance}",
                c.Id, c.Referent.Kind, c.Site, c.Current, c.Rank, c.Referent.Provenance);
        var answers = await gateway.AskAsync(new { utterance }, new Dictionary<string, JevQuestionDto>
        {
            ["referent"] = new("choice",
                "The user refers back to something VoiceOS earlier opened or used. Which offered candidate does the reference denote? Choose one candidate ID only when exactly one is the clear match, using the candidate title and details. Choose AMBIGUOUS when several plausibly match. Choose NONE when the reference is not to any offered candidate (for example, it names something new).",
                ids),
            ["relation"] = new("choice",
                "How does the reference relate to the earlier things?",
                new Dictionary<string, string>
                {
                    ["SAME"] = "One specific earlier thing, referred to as it, that, or that page.",
                    ["ALTERNATIVE"] = "The other one of a pair or set, meaning not the one in view.",
                    ["PREVIOUS"] = "The one before, the earlier one.",
                    ["PROPERTY"] = "Identified by a described property such as a name, year, or kind.",
                    ["NONE_OF_THESE"] = "Not a reference to earlier things."
                })
        }, cancellationToken).ConfigureAwait(false);
        foreach (var head in answers)
            JevDiagnostics.Log(logger, "Referent", head.Key, head.Value);
        var threshold = Math.Max(confidenceThreshold, .7);
        if (!answers.TryGetValue("referent", out var pick) || pick.QuestionType != "choice" || pick.SelectedChoice is null)
            return new(ReferentChoiceKind.Unavailable);
        var relation = answers.TryGetValue("relation", out var rel) && rel.QuestionType == "choice" && rel.Confidence >= confidenceThreshold
            ? rel.SelectedChoice switch
            {
                "SAME" => ReferenceRelation.Same, "ALTERNATIVE" => ReferenceRelation.Alternative,
                "PREVIOUS" => ReferenceRelation.Previous, "PROPERTY" => ReferenceRelation.Property, _ => ReferenceRelation.None
            } : ReferenceRelation.None;
        // A pick too weak to use still carries the relation: the resolver may settle it from the established pair.
        if (pick.Confidence < confidenceThreshold) return new(ReferentChoiceKind.Ambiguous, null, relation);
        logger?.LogInformation("Referent selected={Selection} confidence={Confidence:F2} relation={Relation}",
            pick.SelectedChoice, pick.Confidence, relation);
        if (pick.SelectedChoice == "NONE") return new(ReferentChoiceKind.None);
        if (pick.SelectedChoice == "AMBIGUOUS") return new(ReferentChoiceKind.Ambiguous, null, relation);
        if (!ids.ContainsKey(pick.SelectedChoice)) return new(ReferentChoiceKind.Unavailable);
        // A weakly held pick is not a unique one.
        if (pick.Confidence < threshold) return new(ReferentChoiceKind.Ambiguous, null, relation);
        return new(ReferentChoiceKind.Selected, pick.SelectedChoice, relation);
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
        foreach (var head in answers)
            JevDiagnostics.Log(logger, "Installed app", head.Key, head.Value);
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
