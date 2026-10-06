using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Interaction;

public sealed class EffectLedgerTests
{
    private static readonly InteractionAction Click = new("r1:click:e1", InteractionActionKind.Activate, "e1");

    private static Effect Fx(EffectKind kind, string? actionId = null) => new(kind, EffectSource.CompanionResponse,
        EffectStrength.Observed) { ActionId = actionId };

    [Fact]
    public void Ledger_IsAppendOnlyAndStampsIdsStepAndAction()
    {
        var ledger = new EffectLedger("s7");
        ledger.Append([Fx(EffectKind.SurfaceAcquired)]);
        ledger.Append([Fx(EffectKind.Activated), Fx(EffectKind.Navigated)], "r1:click:e1");
        ledger.Append(null, "ignored");

        Assert.Equal(["s7.1", "s7.2", "s7.3"], ledger.All.Select(static e => e.Id));
        Assert.All(ledger.All, e => Assert.Equal("s7", e.StepId));
        Assert.Equal([null, "r1:click:e1", "r1:click:e1"], ledger.All.Select(static e => e.ActionId));
        Assert.Equal([EffectKind.SurfaceAcquired, EffectKind.Activated, EffectKind.Navigated],
            ledger.All.Select(static e => e.Kind));
    }

    [Fact]
    public void Ledger_KeepsAnActionIdTheEmitterAlreadySet()
    {
        var ledger = new EffectLedger();
        ledger.Append([Fx(EffectKind.Activated, "emitter")], "engine");
        Assert.Equal("emitter", ledger.All[0].ActionId);
    }

    [Fact]
    public void Summary_OmitsTypedValuesLabelsAndUrls()
    {
        var subject = new TypedRef(3, "sess", 4, "e9", "fp", "secret label", "textbox", "https://x.test/p", "secret");
        var effect = new Effect(EffectKind.TextSet, EffectSource.CompanionResponse, EffectStrength.Observed, subject,
            new Dictionary<string, string> { ["value"] = "secret", ["matched"] = "true", ["to"] = "https://x.test/p" })
        { ActionId = "a" };
        var summary = effect.Summarize();
        Assert.Contains("kind=TextSet", summary);
        Assert.Contains("subject=e9@r4(textbox)", summary);
        Assert.Contains("matched=true", summary);
        Assert.DoesNotContain("secret", summary);
        Assert.DoesNotContain("x.test", summary);
    }

    [Fact]
    public async Task Engine_AppendsInitialActionAndPostObservationEffects_InOrderWithActionIds()
    {
        var surface = new EffectSurface();
        var result = await new InteractionEngine().RunAsync(new("do it"), surface,
            new Decisions(InteractionDecision.Act(Click), InteractionDecision.Done()), new InteractionBudget(5, 5, 5));

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal([EffectKind.SurfaceAcquired, EffectKind.Activated, EffectKind.ContentChanged],
            result.Effects!.Select(static e => e.Kind));
        Assert.Equal([null, Click.Id, Click.Id], result.Effects!.Select(static e => e.ActionId));
        Assert.Equal(["s1.1", "s1.2", "s1.3"], result.Effects!.Select(static e => e.Id));
    }

    [Fact]
    public async Task Engine_EmitsEngineNoEffect_WhenSuccessDoesNotChangeState()
    {
        var surface = new EffectSurface(changeState: false);
        var result = await new InteractionEngine().RunAsync(new("do it"), surface,
            new Decisions(InteractionDecision.Act(Click)), new InteractionBudget(5, 5, 1));

        Assert.Equal(InteractionResultStatus.NoEffect, Assert.Single(result.RecentHistory).Result.Status);
        var noEffect = result.Effects!.Last();
        Assert.Equal(EffectKind.NoEffect, noEffect.Kind);
        Assert.Equal(EffectSource.EngineRule, noEffect.Source);
        Assert.Equal("state_unchanged", noEffect.Get("reason"));
        Assert.Equal(Click.Id, noEffect.ActionId);
    }

    [Fact]
    public async Task Engine_DoesNotDuplicateNoEffect_WhenTheSurfaceAlreadyReportedIt()
    {
        var surface = new EffectSurface(changeState: false, failure: true);
        var result = await new InteractionEngine().RunAsync(new("do it"), surface,
            new Decisions(InteractionDecision.Act(Click)), new InteractionBudget(5, 5, 1));

        Assert.Single(result.Effects!, static e => e.Kind == EffectKind.NoEffect);
    }

    [Fact]
    public async Task Engine_ControlFlowIsIdentical_WithAndWithoutEffects()
    {
        async Task<InteractionRunResult> Run(bool withEffects) => await new InteractionEngine().RunAsync(new("do it"),
            new EffectSurface(emit: withEffects), new Decisions(InteractionDecision.Act(Click), InteractionDecision.Done()),
            new InteractionBudget(5, 5, 5));

        var plain = await Run(false);
        var rich = await Run(true);
        Assert.Equal(plain.Completion, rich.Completion);
        Assert.Equal(plain.Progress, rich.Progress);
        Assert.Equal(plain.RecentHistory.Select(static h => (h.Action, h.Result.Status, h.ResultingStateKey)),
            rich.RecentHistory.Select(static h => (h.Action, h.Result.Status, h.ResultingStateKey)));
        Assert.Empty(plain.Effects!);
    }

    private sealed class Decisions(params InteractionDecision[] decisions) : IInteractionDecisionSource
    {
        private readonly Queue<InteractionDecision> _queue = new(decisions);
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
            => ValueTask.FromResult(_queue.Dequeue());
    }

    private sealed class EffectSurface(bool changeState = true, bool failure = false, bool emit = true) : IInteractionSurface
    {
        private int _n;
        private InteractionObservation Next()
        {
            var n = ++_n;
            var key = changeState ? $"k{n}" : "k";
            return new(n, key, "{}", [new("e1", "Go", [Click with { Id = $"r{n}:click:e1" }])],
                emit && n == 1 ? [new(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed)]
                : emit && changeState && n == 2 ? [new(EffectKind.ContentChanged, EffectSource.SnapshotDelta, EffectStrength.Derived)]
                : null);
        }
        private InteractionObservation? _current;
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken ct = default)
        {
            if (_current is null) return ValueTask.FromResult(_current = Next());
            return ValueTask.FromResult(_current with { Effects = null }); // a re-observation reports no new effects
        }
        public ValueTask<InteractionObservation> ObserveAfterActionAsync(CancellationToken ct = default)
            => ValueTask.FromResult(_current = Next());
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation,
            CancellationToken ct = default)
        {
            if (failure)
                return ValueTask.FromResult(InteractionActionResult.Fail(InteractionResultStatus.NoEffect, "nope",
                    [new(EffectKind.NoEffect, EffectSource.CompanionResponse, EffectStrength.Observed)]));
            return ValueTask.FromResult(InteractionActionResult.Ok(effects: emit
                ? [new(EffectKind.Activated, EffectSource.CompanionResponse, EffectStrength.Observed)] : null));
        }
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal,
            InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> history, CancellationToken ct = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Complete));
    }
}
