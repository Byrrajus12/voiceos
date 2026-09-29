using VoiceOS.Core.Activation;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class FrontDoorArbiterTests
{
    private sealed class Catalog : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetAll() => [
            new("charmap", "Character Map", "charmap", AppLaunchKind.Win32, "charmap.exe"),
            new("calculator", "Calculator", "calc", AppLaunchKind.Win32, "calc.exe"),
            new("notepad", "Notepad", "notepad", AppLaunchKind.Win32, "notepad.exe"),
            new("explorer", "File Explorer", "explorer", AppLaunchKind.Win32, "explorer.exe"),
            new("chrome", "Chrome", "chrome", AppLaunchKind.Win32, "chrome.exe")];
        public AppEntry? FindById(string id) => GetAll().FirstOrDefault(a => a.Id == id);
    }
    private static readonly Catalog Apps = new();
    private static CommandRouteDecision Route => new(CommandRoute.Clarify, .3, Reason: RoutingReason.LowConfidence,
        MediaRequestKind: MediaRequestKind.None);
    private static FrontDoorContext Context(bool browser = false) => new(
        new(browser ? "Chrome" : "File Explorer", "", browser ? ForegroundKind.Browser : ForegroundKind.NativeApp, 42),
        [], new(browser, 1, null, []), null);
    private static JevAnswer Choice(string value) => new("choice", value, new Dictionary<string, double>(), .95);
    private static DecisionResult Direct(string utterance, VoiceAction action, string? app = null, bool named = false)
    {
        var answers = new Dictionary<string, JevAnswer> {
            ["is_command"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .99 }, .99),
            ["unit_count"] = Choice("1"), ["unit_1_action_kind"] = Choice(action.ToString()),
            ["unit_1_window_target_mode"] = Choice(named ? "Named" : "Current"),
            ["unit_1_snap_dir"] = Choice("Left"), ["unit_1_volume_direction"] = Choice("Up"),
            ["unit_1_media_op"] = Choice("Pause") };
        if (app is not null) answers["unit_1_target_app"] = Choice(app);
        var state = new DecisionState(utterance, "Explorer", Apps.GetAll().Select(a => new AppCandidate(a.Id, a.DisplayName, a.ProcessName)).ToArray(),
            [], [MediaOperation.Pause], [SnapDirection.Left]);
        var program = new SemanticProgramPlanner(.35, .40).TryBuildProgram(answers, state, new Dictionary<string, string>());
        var plan = new VoicePlan(action, AppCandidateId: app, WindowTargetMode: named ? WindowTargetMode.Named : WindowTargetMode.Current,
            WindowAppCandidateId: named ? app : null, Snap: SnapDirection.Left, VolumeAdjust: VolumeDirection.Up,
            Media: MediaOperation.Pause, Confidence: .95);
        return new(plan, program, answers, 20, 1, 1);
    }

    [Theory]
    [InlineData("Close Character Map.", VoiceAction.CloseCurrentWindow, "charmap", true)]
    [InlineData("Quit Character Map", VoiceAction.CloseCurrentWindow, "charmap", true)]
    [InlineData("Shut the Character Map window", VoiceAction.CloseCurrentWindow, "charmap", true)]
    [InlineData("Exit Calculator", VoiceAction.CloseCurrentWindow, "calculator", true)]
    [InlineData("Close Notepad please", VoiceAction.CloseCurrentWindow, "notepad", true)]
    [InlineData("Turn it up a little.", VoiceAction.AdjustVolume, null, false)]
    [InlineData("A bit louder", VoiceAction.AdjustVolume, null, false)]
    [InlineData("Make it quieter", VoiceAction.AdjustVolume, null, false)]
    [InlineData("Turn the sound down a touch", VoiceAction.AdjustVolume, null, false)]
    [InlineData("Bump the volume up", VoiceAction.AdjustVolume, null, false)]
    [InlineData("Now snap it to the left.", VoiceAction.SnapCurrentWindow, null, false)]
    [InlineData("Put it on the right half", VoiceAction.SnapCurrentWindow, null, false)]
    [InlineData("Snap that left", VoiceAction.SnapCurrentWindow, null, false)]
    [InlineData("Move it over to the right side", VoiceAction.SnapCurrentWindow, null, false)]
    [InlineData("Go back to File Explorer", VoiceAction.FocusWindow, "explorer", true)]
    public void SafeConcreteProgram_Rescues(string text, VoiceAction action, string? app, bool named)
    {
        var direct = Direct(text, action, app, named);
        Assert.NotNull(direct.Program);
        Assert.Equal(FrontDoorVerdictKind.RescueDirect, FrontDoorArbiter.Evaluate(Route, direct, Context(), Apps).Kind);
        if (action == VoiceAction.AdjustVolume)
            Assert.Equal(FrontDoorVerdictKind.RescueDirect, FrontDoorArbiter.Evaluate(Route, direct, Context(true), Apps).Kind);
    }

    [Theory]
    [InlineData("Close this tab", VoiceAction.CloseCurrentWindow, "web_wording")]
    [InlineData("Close the cookie banner", VoiceAction.CloseCurrentWindow, "browser_host_target")]
    [InlineData("Minimize the video player", VoiceAction.MinimizeCurrentWindow, "browser_host_target")]
    [InlineData("Maximize the picture", VoiceAction.MaximizeCurrentWindow, "browser_host_target")]
    [InlineData("Open the official site", VoiceAction.OpenApp, "browser_host_open")]
    [InlineData("Open YouTube", VoiceAction.OpenApp, "policy_RerouteToBrowser")]
    [InlineData("Play the second video", VoiceAction.MediaControl, "route_not_eligible")]
    [InlineData("Pause the video on this page", VoiceAction.MediaControl, "media_guard")]
    [InlineData("Go back", VoiceAction.MediaControl, "route_not_eligible")]
    [InlineData("Spotify", VoiceAction.OpenApp, "route_not_eligible")]
    [InlineData("Search this site for Pride and Prejudice", VoiceAction.None, "no_program")]
    [InlineData("Open it on the web", VoiceAction.AdjustVolume, "web_wording")]
    public void BrowserAndIncompleteRequests_Refused(string text, VoiceAction action, string reason)
    {
        var route = text switch {
            "Close this tab" => Route with { TabDisposition = TabDisposition.CurrentTab },
            "Open YouTube" => Route with { DestinationKind = SemanticDestinationKind.KnownService, DestinationName = "YouTube", RequestedEntity = RequestedEntityKind.NamedEntity },
            "Play the second video" => Route with { Route = CommandRoute.ComputerUse, MediaRequestKind = MediaRequestKind.ContentSelection },
            "Go back" => Route with { Reason = RoutingReason.UnresolvedReturn },
            "Spotify" => Route with { Reason = RoutingReason.IncompleteIntent },
            "Open it on the web" => Route with { SurfacePreference = SurfacePreference.Browser },
            _ => Route };
        var verdict = FrontDoorArbiter.Evaluate(route, Direct(text, action, action == VoiceAction.OpenApp ? "chrome" : null), Context(true), Apps);
        Assert.Equal(FrontDoorVerdictKind.UseRoute, verdict.Kind);
        Assert.Contains(reason, verdict.Reasons);
    }

    [Theory]
    [InlineData(RoutingReason.IncompleteIntent)]
    [InlineData(RoutingReason.UnresolvedReturn)]
    [InlineData(RoutingReason.RouterFailure)]
    public void ForbiddenRouteReason_NeverRescues(RoutingReason reason)
        => Assert.Contains("route_not_eligible", FrontDoorArbiter.Evaluate(Route with { Reason = reason },
            Direct("snap it", VoiceAction.SnapCurrentWindow), Context(), Apps).Reasons);

    [Fact]
    public void AmbiguousAndNativeFallback_AreEligible()
    {
        var direct = Direct("close Character Map", VoiceAction.CloseCurrentWindow, "charmap", true);
        Assert.Equal(FrontDoorVerdictKind.RescueDirect, FrontDoorArbiter.Evaluate(Route with { Reason = RoutingReason.AmbiguousIntent }, direct, Context(), Apps).Kind);
        var native = Route with { Route = CommandRoute.NativeInteraction };
        Assert.Equal(FrontDoorVerdictKind.UseRoute, FrontDoorArbiter.Evaluate(native, direct, Context(), Apps).Kind);
        Assert.Equal(FrontDoorVerdictKind.RescueDirect, FrontDoorArbiter.Evaluate(native, direct, Context(), Apps, nativeFallback: true).Kind);
    }

    [Theory]
    [InlineData("COMPUTER_USE")]
    [InlineData("TEXT_TRANSFORM")]
    public void RelativeDistributionVeto_PrefersNonDirect(string choice)
    {
        var route = Route with { RawAnswers = new Dictionary<string, JevAnswer> { ["route"] = new("choice", "CLARIFY",
            new Dictionary<string, double> { [choice] = .5, ["DIRECT_CAPABILITY"] = .3, ["CLARIFY"] = .2 }, .3) } };
        Assert.Contains("route_prefers_non_direct", FrontDoorArbiter.Evaluate(route, Direct("louder", VoiceAction.AdjustVolume), Context(), Apps).Reasons);
    }

    [Fact]
    public void CompoundFailure_ProviderFailure_AndInsanePlan_Refused()
    {
        var direct = Direct("snap", VoiceAction.SnapCurrentWindow);
        var compound = new Dictionary<string, JevAnswer> { ["is_compound"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .9 }, .9) };
        Assert.Contains("no_program", FrontDoorArbiter.Evaluate(Route, direct with { Program = null, RawAnswers = compound }, Context(), Apps).Reasons);
        Assert.Contains("direct_unavailable", FrontDoorArbiter.Evaluate(Route, direct with { ProviderFailed = true }, Context(), Apps).Reasons);
        Assert.Contains("plan_not_sane", FrontDoorArbiter.Evaluate(Route, direct with { Plan = direct.Plan with { RequiresClarification = true } }, Context(), Apps).Reasons);
        Assert.Contains("no_program", FrontDoorArbiter.Evaluate(Route, direct with { Program = null, Plan = direct.Plan with { RequiresClarification = true } }, Context(), Apps).Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedBrowserWindowTargets_Refused(bool candidate)
    {
        var direct = Direct("close chrome", VoiceAction.CloseCurrentWindow, "chrome", true);
        if (candidate) direct = direct with { Program = new([new CloseWindowStep("s1", new CandidateWindowTarget("w"))]) };
        var verdict = FrontDoorArbiter.Evaluate(Route, direct, Context(), Apps, [new("w", "chrome", "Chrome")]);
        Assert.Contains("browser_host_target", verdict.Reasons);
    }
}
