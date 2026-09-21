using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// Tests SemanticProgramPlanner: single and compound VoiceProgram construction
/// from injected Jev answer dictionaries (no HTTP, no audio).
///
/// Coverage maps to the M5C.2 spec requirements:
///   single commands, compound explicit targets, step references, shared operations,
///   independent vs reference targets, three-step programs, new-instance references,
///   invalid reference types, and program ordering.
/// </summary>
public class SemanticProgramPlannerTests
{
    // Thresholds matching the runtime defaults
    private const double CommandThreshold = 0.35;
    private const double ActionThreshold = 0.40;

    private static readonly SemanticProgramPlanner Planner =
        new(CommandThreshold, ActionThreshold);

    private static DecisionState MakeState(string transcript = "test", string[]? appIds = null)
    {
        var apps = appIds != null
            ? appIds.Select(id => new AppCandidate(id, id, id)).ToList()
            : new List<AppCandidate>
            {
                new("google-chrome", "Google Chrome", "chrome"),
                new("visual-studio-code", "VS Code", "Code"),
                new("discord", "Discord", "Discord"),
            };
        return new DecisionState(
            transcript, "notepad",
            apps,
            [],
            [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
            [SnapDirection.Left, SnapDirection.Right]);
    }

    private static IReadOnlyDictionary<string, string> NoText => new Dictionary<string, string>();

    // ── Answer builder helpers ────────────────────────────────────────────────

    private static JevAnswer Choice(string value, double confidence = 0.9)
        => new("choice", value, new Dictionary<string, double> { [value] = confidence }, confidence);

    private static JevAnswer Noul(double prob)
        => new("noul", prob >= 0.5 ? "true" : "false",
               new Dictionary<string, double> { ["noul"] = prob }, prob);

    private static void AddCommand(Dictionary<string, JevAnswer> d, double prob = 0.9)
        => d["is_command"] = Noul(prob);

    private static void AddUnit(
        Dictionary<string, JevAnswer> d, int i,
        string action,
        string? targetApp = null,
        string? windowTargetMode = null,
        string? snapDir = null,
        string? activationMode = null,
        string? volumeDir = null,
        string? mediaOp = null,
        double priorRef = 0.0,
        string? monitorTarget = null)
    {
        d[$"unit_{i}_action_kind"] = Choice(action);
        if (targetApp != null) d[$"unit_{i}_target_app"] = Choice(targetApp);
        if (windowTargetMode != null) d[$"unit_{i}_window_target_mode"] = Choice(windowTargetMode);
        if (snapDir != null) d[$"unit_{i}_snap_dir"] = Choice(snapDir);
        if (activationMode != null) d[$"unit_{i}_activation_mode"] = Choice(activationMode);
        if (volumeDir != null) d[$"unit_{i}_volume_direction"] = Choice(volumeDir);
        if (mediaOp != null) d[$"unit_{i}_media_op"] = Choice(mediaOp);
        if (i > 1) d[$"unit_{i}_prior_ref"] = Noul(priorRef);
        if (monitorTarget != null) d[$"unit_{i}_monitor_target"] = Choice(monitorTarget);
    }

    // ── Single-command tests ──────────────────────────────────────────────────

    [Fact]
    public void Single_OpenChrome_ProducesOneOpenAppStep()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal("s1", step.StepId);
        Assert.Equal("google-chrome", step.App.AppCandidateId);
        Assert.Equal(AppActivationMode.FocusOrLaunch, step.ActivationMode);
    }

    [Fact]
    public void Single_SnapVSCodeLeft_ProducesOneSnapWindowStep()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "SnapCurrentWindow",
            targetApp: "visual-studio-code",
            windowTargetMode: "Named",
            snapDir: "Left");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<SnapWindowStep>(program.Steps[0]);
        Assert.Equal("s1", step.StepId);
        Assert.Equal(SnapDirection.Left, step.Direction);
        var target = Assert.IsType<AppTarget>(step.Target);
        Assert.Equal("visual-studio-code", target.AppCandidateId);
    }

    [Fact]
    public void Single_TurnVolumeDown_ProducesOneAdjustVolumeStep()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Down");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<AdjustVolumeStep>(program.Steps[0]);
        Assert.Equal("s1", step.StepId);
        Assert.Equal(VolumeDirection.Down, step.Direction);
    }

    [Fact]
    public void Single_MediaPlay_ProducesOneMediaControlStep()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MediaControl", mediaOp: "Play");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MediaControlStep>(program!.Steps[0]);
        Assert.Equal(MediaOperation.Play, step.Operation);
    }

    [Fact]
    public void Single_CurrentWindow_MaximizeThis_UsesCurrentWindowTarget()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MaximizeCurrentWindow", windowTargetMode: "Current");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MaximizeWindowStep>(program!.Steps[0]);
        Assert.IsType<CurrentWindowTarget>(step.Target);
    }

    // ── Compound: explicit independent targets ────────────────────────────────

    [Fact]
    public void Compound_OpenChrome_MinimizeDiscord_TwoStepsExplicitTargets()
    {
        // "Open Chrome and minimize Discord"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "MinimizeCurrentWindow",
            targetApp: "discord",
            windowTargetMode: "Named",
            priorRef: 0.1);  // not a prior ref

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal("google-chrome", s1.App.AppCandidateId);

        var s2 = Assert.IsType<MinimizeWindowStep>(program.Steps[1]);
        var t2 = Assert.IsType<AppTarget>(s2.Target);
        Assert.Equal("discord", t2.AppCandidateId);
    }

    // ── Compound: step-result reference ("maximize it") ──────────────────────

    [Fact]
    public void Compound_OpenChrome_MaximizeIt_UsesStepResultTarget()
    {
        // "Open Chrome and maximize it"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "MaximizeCurrentWindow", priorRef: 0.9);  // "it" = s1

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal("s1", s1.StepId);

        var s2 = Assert.IsType<MaximizeWindowStep>(program.Steps[1]);
        var target = Assert.IsType<StepResultTarget>(s2.Target);
        Assert.Equal("s1", target.StepId);
    }

    // ── Compound: independent target (not prior ref) ──────────────────────────

    [Fact]
    public void Compound_OpenChrome_MaximizeVSCode_IsAppTarget_NotStepResult()
    {
        // "Open Chrome and maximize VS Code" — independent target, NOT a prior-step reference
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "MaximizeCurrentWindow",
            targetApp: "visual-studio-code",
            windowTargetMode: "Named",
            priorRef: 0.1);  // not a reference

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var s2 = Assert.IsType<MaximizeWindowStep>(program!.Steps[1]);
        var target = Assert.IsType<AppTarget>(s2.Target);
        Assert.Equal("visual-studio-code", target.AppCandidateId);
    }

    // ── Compound: shared operation ("Open Chrome and Discord") ────────────────

    [Fact]
    public void Compound_OpenChromeAndDiscord_TwoOpenAppSteps()
    {
        // "Open Chrome and Discord" — same verb, two app targets
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "OpenApp", targetApp: "discord", priorRef: 0.05);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal("google-chrome", s1.App.AppCandidateId);

        var s2 = Assert.IsType<OpenAppStep>(program.Steps[1]);
        Assert.Equal("discord", s2.App.AppCandidateId);
    }

    // ── Compound: two independent window operations ───────────────────────────

    [Fact]
    public void Compound_SnapChromeRight_VSCodeLeft_TwoIndependentSnaps()
    {
        // "Snap Chrome right and VS Code left"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "SnapCurrentWindow",
            targetApp: "google-chrome",
            windowTargetMode: "Named",
            snapDir: "Right");
        AddUnit(answers, 2, "SnapCurrentWindow",
            targetApp: "visual-studio-code",
            windowTargetMode: "Named",
            snapDir: "Left",
            priorRef: 0.05);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<SnapWindowStep>(program.Steps[0]);
        Assert.Equal(SnapDirection.Right, s1.Direction);
        Assert.Equal("google-chrome", ((AppTarget)s1.Target).AppCandidateId);

        var s2 = Assert.IsType<SnapWindowStep>(program.Steps[1]);
        Assert.Equal(SnapDirection.Left, s2.Direction);
        Assert.Equal("visual-studio-code", ((AppTarget)s2.Target).AppCandidateId);
    }

    // ── Compound: three heterogeneous steps ───────────────────────────────────

    [Fact]
    public void Compound_ThreeSteps_OpenChrome_VolumeDown_MinimizeDiscord()
    {
        // "Open Chrome, turn the volume down, and minimize Discord"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("3");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "AdjustVolume", volumeDir: "Down", priorRef: 0.05);
        AddUnit(answers, 3, "MinimizeCurrentWindow",
            targetApp: "discord",
            windowTargetMode: "Named",
            priorRef: 0.05);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(3, program!.Steps.Count);

        Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.IsType<AdjustVolumeStep>(program.Steps[1]);
        var s3 = Assert.IsType<MinimizeWindowStep>(program.Steps[2]);
        Assert.Equal("discord", ((AppTarget)s3.Target).AppCandidateId);
    }

    [Fact]
    public void Compound_ThreeSteps_OrderIsDeterministic()
    {
        // Steps must appear in s1, s2, s3 order
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("3");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");
        AddUnit(answers, 2, "AdjustVolume", volumeDir: "Down", priorRef: 0.05);
        AddUnit(answers, 3, "MinimizeCurrentWindow",
            targetApp: "discord",
            windowTargetMode: "Named",
            priorRef: 0.05);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal("s1", program!.Steps[0].StepId);
        Assert.Equal("s2", program.Steps[1].StepId);
        Assert.Equal("s3", program.Steps[2].StepId);
    }

    // ── Compound: new-instance reference ─────────────────────────────────────

    [Fact]
    public void Compound_NewChromeWindow_SnapRight_StepResultTarget()
    {
        // "Open a new Chrome window and snap it right"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp",
            targetApp: "google-chrome",
            activationMode: "NewInstance");
        AddUnit(answers, 2, "SnapCurrentWindow",
            snapDir: "Right",
            priorRef: 0.9);  // "it" references s1

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal(AppActivationMode.NewInstance, s1.ActivationMode);
        Assert.Equal("google-chrome", s1.App.AppCandidateId);

        var s2 = Assert.IsType<SnapWindowStep>(program.Steps[1]);
        Assert.Equal(SnapDirection.Right, s2.Direction);
        var ref2 = Assert.IsType<StepResultTarget>(s2.Target);
        Assert.Equal("s1", ref2.StepId);
    }

    // ── Invalid reference type safety ─────────────────────────────────────────

    [Fact]
    public void Compound_VolumeDown_MaximizeIt_ReturnsNull_InvalidReference()
    {
        // "Turn the volume down and maximize it"
        // AdjustVolumeStep does NOT produce a window → StepResultTarget is invalid.
        // Must return null, not create an executable program with bogus reference.
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Down");
        AddUnit(answers, 2, "MaximizeCurrentWindow", priorRef: 0.9);  // "it" = s1, invalid

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.Null(program);  // safe clarification path — not an executable program
    }

    [Fact]
    public void Compound_MediaControl_SnapIt_ReturnsNull_InvalidReference()
    {
        // MediaControlStep cannot yield a window; snap "it" is invalid
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "MediaControl", mediaOp: "Play");
        AddUnit(answers, 2, "SnapCurrentWindow", snapDir: "Left", priorRef: 0.9);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.Null(program);
    }

    // ── Gateway: low command confidence ──────────────────────────────────────

    [Fact]
    public void Gateway_LowCommandConfidence_ReturnsNull()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers, prob: 0.1);  // below 0.35 threshold
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.Null(program);
    }

    [Fact]
    public void Gateway_MissingIsCommand_ReturnsNull()
    {
        var answers = new Dictionary<string, JevAnswer>();
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.Null(program);
    }

    // ── Prior-ref resolves to most-recent window-yielding step ───────────────

    [Fact]
    public void Compound_VolumeStep_Then_OpenChrome_SnapIt_RefersToChrome()
    {
        // Volume step (no window), Open Chrome (window), snap "it" → s2 (Chrome)
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("3");
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Up");
        AddUnit(answers, 2, "OpenApp", targetApp: "google-chrome", priorRef: 0.05);
        AddUnit(answers, 3, "SnapCurrentWindow", snapDir: "Right", priorRef: 0.9);  // "it" = s2

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(3, program!.Steps.Count);

        var s3 = Assert.IsType<SnapWindowStep>(program.Steps[2]);
        var ref3 = Assert.IsType<StepResultTarget>(s3.Target);
        Assert.Equal("s2", ref3.StepId);  // resolves to Open Chrome (s2), not volume (s1)
    }

    // ── is_compound fallback path ─────────────────────────────────────────────

    [Fact]
    public void Compound_IsCompoundFallback_WhenUnitCountMissing()
    {
        // When unit_count is absent but is_compound is true, should try to build 1 unit
        // (since we default to 1 when unit_count is missing)
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["is_compound"] = Noul(0.9);  // compound, but no unit_count → falls back to 1
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Down");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        // Without unit_count, defaults to 1 → single step from unit_1
        Assert.NotNull(program);
        Assert.Single(program!.Steps);
    }

    // ── MoveWindow: simple semantics ──────────────────────────────────────────

    [Fact]
    public void Single_MoveWindowToOtherMonitor_CurrentWindowTarget()
    {
        // "move this to the other monitor"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow", windowTargetMode: "Current", monitorTarget: "Other");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        Assert.IsType<CurrentWindowTarget>(step.Target);
        Assert.IsType<OtherMonitor>(step.Monitor);
    }

    [Fact]
    public void Single_MoveWindowToInternalMonitor_NamedChrome()
    {
        // "move Chrome to my laptop screen"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow",
            targetApp: "google-chrome",
            windowTargetMode: "Named",
            monitorTarget: "Internal");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        var target = Assert.IsType<AppTarget>(step.Target);
        Assert.Equal("google-chrome", target.AppCandidateId);
        Assert.IsType<InternalMonitor>(step.Monitor);
    }

    [Fact]
    public void Single_MoveWindowToExternalMonitor_NamedTarget()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow",
            targetApp: "visual-studio-code",
            windowTargetMode: "Named",
            monitorTarget: "External");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        Assert.IsType<AppTarget>(step.Target);
        Assert.IsType<ExternalMonitor>(step.Monitor);
    }

    [Theory]
    [InlineData("Left",  RelativeMonitorDirection.Left)]
    [InlineData("Right", RelativeMonitorDirection.Right)]
    [InlineData("Above", RelativeMonitorDirection.Above)]
    [InlineData("Below", RelativeMonitorDirection.Below)]
    public void Single_MoveWindowRelativeDirection_ProducesRelativeMonitor(
        string dirString, RelativeMonitorDirection expected)
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow", windowTargetMode: "Current", monitorTarget: dirString);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        Assert.IsType<CurrentWindowTarget>(step.Target);
        var monitor = Assert.IsType<RelativeMonitor>(step.Monitor);
        Assert.Equal(expected, monitor.Direction);
    }

    [Fact]
    public void Single_MoveWindowToPrimaryMonitor()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow", windowTargetMode: "Current", monitorTarget: "Primary");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        Assert.IsType<CurrentWindowTarget>(step.Target);
        Assert.IsType<PrimaryMonitor>(step.Monitor);
    }

    [Fact]
    public void Single_MoveWindowToExternalMonitor_MyMonitorPhrasing_ProducesExternalMonitor()
    {
        // Regression: "my monitor" / "my external monitor" should produce ExternalMonitor,
        // not UnsupportedExplicitTarget. Jev must select External for these natural phrases.
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow",
            targetApp: "google-chrome",
            windowTargetMode: "Named",
            monitorTarget: "External");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        Assert.IsType<ExternalMonitor>(step.Monitor);
    }

    // ── MoveWindow: unsupported ordinal target ────────────────────────────────

    [Fact]
    public void Single_MoveWindow_UnsupportedOrdinalTarget_ProducesNoExecutableStep()
    {
        // Regression: explicit ordinal/numeric references ('monitor 2', 'second monitor',
        // 'display 1') are represented as UnsupportedExplicitTarget in Jev answers.
        // ParseMonitorTarget returns null → MoveWindowStep cannot be built → program is null.
        // The ordinal must NOT be silently aliased to OtherMonitor or ExternalMonitor.
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "MoveWindow",
            windowTargetMode: "Current",
            monitorTarget: "UnsupportedExplicitTarget");

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.Null(program);  // requires clarification — non-executable
    }

    // ── MoveWindow: compound programs ────────────────────────────────────────

    [Fact]
    public void Compound_OpenChrome_MoveToOtherMonitor_StepResultReference()
    {
        // "open a new Chrome window and move it to the other monitor"
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "OpenApp", targetApp: "google-chrome", activationMode: "NewInstance");
        AddUnit(answers, 2, "MoveWindow", monitorTarget: "Other", priorRef: 0.9);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal(AppActivationMode.NewInstance, s1.ActivationMode);
        Assert.Equal("google-chrome", s1.App.AppCandidateId);

        var s2 = Assert.IsType<MoveWindowStep>(program.Steps[1]);
        var ref2 = Assert.IsType<StepResultTarget>(s2.Target);
        Assert.Equal("s1", ref2.StepId);
        Assert.IsType<OtherMonitor>(s2.Monitor);
    }

    [Fact]
    public void Compound_MinimizeVSCode_MoveChromeToLaptopScreen_TwoIndependentSteps()
    {
        // "minimize VS Code and move Chrome to the laptop screen"
        // MoveWindow must compose without disrupting existing compound unit construction
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "MinimizeCurrentWindow",
            targetApp: "visual-studio-code",
            windowTargetMode: "Named");
        AddUnit(answers, 2, "MoveWindow",
            targetApp: "google-chrome",
            windowTargetMode: "Named",
            monitorTarget: "Internal",
            priorRef: 0.05);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        Assert.NotNull(program);
        Assert.Equal(2, program!.Steps.Count);

        var s1 = Assert.IsType<MinimizeWindowStep>(program.Steps[0]);
        Assert.Equal("visual-studio-code", ((AppTarget)s1.Target).AppCandidateId);

        var s2 = Assert.IsType<MoveWindowStep>(program.Steps[1]);
        var s2Target = Assert.IsType<AppTarget>(s2.Target);
        Assert.Equal("google-chrome", s2Target.AppCandidateId);
        Assert.IsType<InternalMonitor>(s2.Monitor);
    }

    // ── ToSingleStepProgram adapter ───────────────────────────────────────────

    [Fact]
    public void Adapter_OpenAppPlan_ProducesOneOpenAppStep()
    {
        var plan = new VoicePlan(
            VoiceAction.OpenApp,
            AppCandidate: "Google Chrome",
            AppCandidateId: "google-chrome",
            ActivationMode: AppActivationMode.FocusOrLaunch);

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<OpenAppStep>(program.Steps[0]);
        Assert.Equal("google-chrome", step.App.AppCandidateId);
    }

    [Fact]
    public void Adapter_NoneAction_ReturnsNull()
    {
        var plan = new VoicePlan(VoiceAction.None);
        Assert.Null(SemanticProgramPlanner.ToSingleStepProgram(plan));
    }

    [Fact]
    public void Adapter_RequiresClarification_ReturnsNull()
    {
        var plan = new VoicePlan(VoiceAction.OpenApp, RequiresClarification: true);
        Assert.Null(SemanticProgramPlanner.ToSingleStepProgram(plan));
    }

    [Fact]
    public void Adapter_SnapCurrentWindowPlan_CurrentMode_UsesCurrentWindowTarget()
    {
        var plan = new VoicePlan(
            VoiceAction.SnapCurrentWindow,
            Snap: SnapDirection.Right,
            WindowTargetMode: WindowTargetMode.Current);

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        var step = Assert.IsType<SnapWindowStep>(program!.Steps[0]);
        Assert.Equal(SnapDirection.Right, step.Direction);
        Assert.IsType<CurrentWindowTarget>(step.Target);
    }

    [Fact]
    public void Adapter_SnapNamedWindowPlan_UsesCandidateWindowTarget()
    {
        var plan = new VoicePlan(
            VoiceAction.SnapCurrentWindow,
            Snap: SnapDirection.Left,
            WindowTargetMode: WindowTargetMode.Named,
            WindowCandidateId: "w-chrome");

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        var step = Assert.IsType<SnapWindowStep>(program!.Steps[0]);
        var target = Assert.IsType<CandidateWindowTarget>(step.Target);
        Assert.Equal("w-chrome", target.WindowCandidateId);
    }

    [Fact]
    public void Adapter_AdjustVolumePlan_ProducesAdjustVolumeStep()
    {
        var plan = new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: VolumeDirection.Down);
        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        var step = Assert.IsType<AdjustVolumeStep>(program!.Steps[0]);
        Assert.Equal(VolumeDirection.Down, step.Direction);
    }

    // ── ToSingleStepProgram adapter: MoveWindow ───────────────────────────────

    [Fact]
    public void Adapter_MoveWindow_CurrentMode_OtherMonitor_ProducesMoveWindowStep()
    {
        var plan = new VoicePlan(
            VoiceAction.MoveWindow,
            WindowTargetMode: WindowTargetMode.Current,
            MonitorMove: new OtherMonitor());

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<MoveWindowStep>(program.Steps[0]);
        Assert.Equal("s1", step.StepId);
        Assert.IsType<CurrentWindowTarget>(step.Target);
        Assert.IsType<OtherMonitor>(step.Monitor);
    }

    [Fact]
    public void Adapter_MoveWindow_NamedChrome_InternalMonitor_UsesCandidateWindowTarget()
    {
        var plan = new VoicePlan(
            VoiceAction.MoveWindow,
            WindowTargetMode: WindowTargetMode.Named,
            WindowCandidateId: "chrome-win-1",
            MonitorMove: new InternalMonitor());

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<MoveWindowStep>(program!.Steps[0]);
        var target = Assert.IsType<CandidateWindowTarget>(step.Target);
        Assert.Equal("chrome-win-1", target.WindowCandidateId);
        Assert.IsType<InternalMonitor>(step.Monitor);
    }

    [Fact]
    public void Adapter_MoveWindow_NullMonitorMove_ReturnsNull()
    {
        var plan = new VoicePlan(
            VoiceAction.MoveWindow,
            WindowTargetMode: WindowTargetMode.Current,
            MonitorMove: null);

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.Null(program);
    }

    // ── Compound-detected, invalid ref: guard against single-plan fallback ───

    [Fact]
    public void Compound_FailedInvalidRef_ReturnsNull_NotFallbackPlan()
    {
        // "Play music and snap it right": is_compound fires, step1=MediaControl (no window),
        // step2=SnapCurrentWindow with prior_ref → TryBuildProgram must return null.
        // The caller (orchestrator) must NOT then fall back to ToSingleStepProgram(flat plan),
        // which would silently execute only the MediaControl step.
        // This test verifies TryBuildProgram returns null for the compound case.
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["is_compound"] = Noul(0.85);
        answers["unit_count"] = Choice("2");
        AddUnit(answers, 1, "MediaControl", mediaOp: "Play");
        AddUnit(answers, 2, "SnapCurrentWindow", snapDir: "Right", priorRef: 0.9);

        var program = Planner.TryBuildProgram(answers, MakeState(), NoText);

        // Compound fails — MediaControlStep cannot yield a window for the prior ref.
        Assert.Null(program);

        // Confirm that the flat plan WOULD produce a program via the adapter,
        // documenting why the orchestrator must not apply ToSingleStepProgram
        // when is_compound was detected.
        var flatPlan = new VoicePlan(VoiceAction.MediaControl, Media: MediaOperation.Play);
        var flatProgram = SemanticProgramPlanner.ToSingleStepProgram(flatPlan);
        Assert.NotNull(flatProgram);  // the fallback exists but must not be used for compound
    }
}
