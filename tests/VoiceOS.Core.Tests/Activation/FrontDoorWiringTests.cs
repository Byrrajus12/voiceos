using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class FrontDoorWiringTests
{
    private sealed class Engine : IDecisionEngine
    {
        public int Calls;
        public Func<Task<DecisionResult>>? Handler;
        public Task<DecisionResult> DecideAsync(DecisionState state, CancellationToken ct = default)
        { Calls++; return Handler?.Invoke() ?? Task.FromResult(VolumeDecision()); }
    }
    private static DecisionResult VolumeDecision() => new(new(VoiceAction.AdjustVolume, VolumeAdjust: VolumeDirection.Up),
        new([new AdjustVolumeStep("s1", VolumeDirection.Up)]), new Dictionary<string, JevAnswer>(), 0, 0, 0);
    private sealed class Router(CommandRoute route) : ICommandRouter
    {
        public Func<Task>? Handler;
        public SemanticEndState EndState;
        public async ValueTask<CommandRouteDecision> RouteAsync(string transcript, CancellationToken cancellationToken = default)
        {
            if (Handler is not null) await Handler();
            return new(route, .3, Reason: RoutingReason.LowConfidence, MediaRequestKind: MediaRequestKind.None,
                DestinationKind: route == CommandRoute.ComputerUse ? SemanticDestinationKind.KnownService : SemanticDestinationKind.None,
                DestinationName: route == CommandRoute.ComputerUse ? "YouTube" : null, EndState: EndState);
        }
    }
    private sealed class Volume : IVolumeService
    {
        public int Calls;
        public ExecutionResult SetVolume(int percent) => throw new InvalidOperationException();
        public ExecutionResult AdjustVolume(VolumeDirection direction, int? amount = null)
        { Calls++; return ExecutionResult.Ok("Adjusted"); }
    }
    private static ActivationOrchestrator Create(Router router, Engine engine, Volume volume, bool rescue = true, bool speculative = true)
        => new(new GlobalKeyboardHook(0xA3, NullLogger<GlobalKeyboardHook>.Instance),
            new GlobalKeyboardHook(0x77, NullLogger<GlobalKeyboardHook>.Instance),
            new AudioCaptureService(NullLogger<AudioCaptureService>.Instance), null,
            new VoiceOSConfig { DirectRescue = rescue, SpeculativeDirectDecision = speculative }, NullLogger<ActivationOrchestrator>.Instance,
            decisionEngine: engine, programExecutor: new ProgramExecutor(null!, null!, null!, null!, volume, null!, null!, NullLogger<ProgramExecutor>.Instance),
            commandRouter: router);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClarifyRoute_WithSafeDirectProgram_RunsDirect_ExactlyOnce(bool speculative)
    {
        var engine = new Engine(); var volume = new Volume();
        using var orchestrator = Create(new(CommandRoute.Clarify), engine, volume, speculative: speculative);
        var run = await orchestrator.RunTranscriptAsync("A bit louder");
        Assert.Equal(CommandRoute.Clarify, run.InitialRoute!.Route);
        Assert.Equal(RoutingReason.DirectRescue, run.Route!.Reason);
        Assert.Equal("DirectCapability", run.Lane);
        Assert.Equal(ApplicationInteractionPhase.Succeeded, run.TerminalSnapshot!.Phase);
        Assert.Equal(FrontDoorVerdictKind.RescueDirect, run.FrontDoor!.Kind);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, volume.Calls);
    }

    [Fact]
    public async Task DirectRescueFlagOff_DoesNotInventAQuestion()
    {
        var engine = new Engine(); var volume = new Volume();
        using var orchestrator = Create(new(CommandRoute.Clarify), engine, volume, rescue: false);
        var run = await orchestrator.RunTranscriptAsync("A bit louder");
        Assert.Equal(ApplicationInteractionPhase.Failed, run.TerminalSnapshot!.Phase);
        Assert.Equal(0, volume.Calls);
        Assert.False(run.SpeculativeDirectUsed);
    }

    [Fact]
    public async Task ComputerUseRoute_WithBrowserContentSignal_IsNotRescued()
    {
        var engine = new Engine(); var volume = new Volume();
        using var orchestrator = Create(new(CommandRoute.ComputerUse) { EndState = SemanticEndState.ResourceOpened }, engine, volume);
        var run = await orchestrator.RunTranscriptAsync("Open YouTube");
        Assert.Equal(ExecutionScopeKind.Browser, run.Scope!.Kind);
        Assert.Equal(0, volume.Calls);
        Assert.False(run.SpeculativeDirectUsed);
    }

    [Fact]
    public async Task ConfidentComputerUseRoute_WithoutBrowserContent_YieldsToGroundedDirectProgram()
    {
        var engine = new Engine(); var volume = new Volume();
        using var orchestrator = Create(new(CommandRoute.ComputerUse) { EndState = SemanticEndState.StateChanged }, engine, volume);
        var run = await orchestrator.RunTranscriptAsync("A bit louder");
        Assert.Equal(ExecutionScopeKind.DirectCapability, run.Scope!.Kind);
        Assert.Equal(1, volume.Calls);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task RouterAndDirect_StartInParallel_AndReuseDecision()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Handler = async () => { started.SetResult(); await release.Task; return VolumeDecision(); } };
        var router = new Router(CommandRoute.DirectCapability) { Handler = async () => {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.SetResult(); } };
        var volume = new Volume();
        using var orchestrator = Create(router, engine, volume);
        var run = await orchestrator.RunTranscriptAsync("louder");
        Assert.Null(run.Failure);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, volume.Calls);
        Assert.True(run.SpeculativeDirectUsed);
    }

    [Fact]
    public async Task UnusedSpeculativeException_IsObserved_AndDoesNotBlockBrowserRoute()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Handler = async () => { await release.Task; throw new InvalidOperationException("provider unavailable"); } };
        var volume = new Volume();
        using var orchestrator = Create(new(CommandRoute.ComputerUse), engine, volume);
        var run = await orchestrator.RunTranscriptAsync("Open YouTube").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.SpeculativeDirectUsed);
        Assert.False(run.SpeculativeTask!.IsCompleted);
        release.SetResult();
        var result = await run.SpeculativeTask;
        Assert.True(result.ProviderFailed);
        Assert.Equal(0, volume.Calls);
    }
}
