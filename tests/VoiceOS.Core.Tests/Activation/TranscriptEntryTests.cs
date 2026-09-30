using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Config;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class TranscriptEntryTests
{
    private sealed class FixedRouter(CommandRouteDecision decision) : ICommandRouter
    {
        public List<string> Transcripts { get; } = [];

        public ValueTask<CommandRouteDecision> RouteAsync(string transcript, CancellationToken cancellationToken = default)
        {
            Transcripts.Add(transcript);
            return ValueTask.FromResult(decision);
        }
    }

    private static ActivationOrchestrator Create(ICommandRouter router) => new(
        new GlobalKeyboardHook(0xA3, NullLogger<GlobalKeyboardHook>.Instance),
        new GlobalKeyboardHook(0x77, NullLogger<GlobalKeyboardHook>.Instance),
        new AudioCaptureService(NullLogger<AudioCaptureService>.Instance),
        debugWriter: null, new VoiceOSConfig(), NullLogger<ActivationOrchestrator>.Instance,
        commandRouter: router);

    [Fact]
    public async Task InjectedTranscript_RunsSharedCommandLifecycle_AndRecordsIt()
    {
        var router = new FixedRouter(new(CommandRoute.Clarify, 0.2, "Which one?", RoutingReason.IncompleteIntent));
        using var orchestrator = Create(router);
        var ui = new List<ProductUiPhase>();
        var states = new List<ActivationState>();
        orchestrator.ProductUiChanged += (_, e) => ui.Add(e.Phase);
        orchestrator.StateChanged += (_, s) => states.Add(s);

        var run = await orchestrator.RunTranscriptAsync("open the thing");

        Assert.Equal(["open the thing"], router.Transcripts);
        Assert.Equal(ActivationSource.InjectedTranscript, run.Source);
        Assert.Equal("Clarify", run.Lane);
        Assert.Equal("Unresolved", run.Outcome);
        Assert.Equal(ExecutionScopeKind.Unresolved, run.Scope?.Kind);
        Assert.Equal(CommandRoute.Clarify, run.Route?.Route);
        Assert.Equal(ApplicationInteractionPhase.Failed, run.TerminalSnapshot?.Phase);
        Assert.Equal(["context", "decision_prep", "route", "scope", "front_door"], run.Trace!.Stages.Select(static s => s.Name));
        Assert.NotNull(run.CompletedAt);
        Assert.Null(run.Failure);
        Assert.Equal(ProductUiPhase.Understanding, ui[0]);
        Assert.Contains(ProductUiPhase.Error, ui);
        Assert.Equal(ActivationState.Idle, states[^1]);
    }

    [Fact]
    public async Task EachInjectedTranscript_IsItsOwnActivation()
    {
        using var orchestrator = Create(new FixedRouter(
            new(CommandRoute.Clarify, 0.2, null, RoutingReason.IncompleteIntent)));

        var first = await orchestrator.RunTranscriptAsync("one");
        var second = await orchestrator.RunTranscriptAsync("two");

        Assert.NotEqual(first.ActivationId, second.ActivationId);
        Assert.Equal("two", second.Transcript);
        Assert.DoesNotContain(first.Snapshots, static s => s.Transcript == "two");
    }
}
