using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>The bind head rides the existing fan-out; it reports a binding and never changes the chosen action.</summary>
public sealed class BrowserBindingTests
{
    private static InteractionObservation Observation(params (string Ref, string Name)[] links)
    {
        var elements = links.Select(l => new { id = l.Ref, Role = "link", Name = l.Name, Value = "", Href = "https://a.test/" + l.Ref });
        var candidates = links.Select(l => new InteractionCandidate(l.Ref, $"link '{l.Name}'",
            [new($"r1:click:{l.Ref}", InteractionActionKind.Activate, l.Ref)])).ToList();
        candidates.Add(new("page", "Current page", [new("r1:scroll:down", InteractionActionKind.Scroll, Direction: "down")]));
        return new(1, "k", JsonSerializer.Serialize(new { current_url = "https://a.test/", current_title = "A", elements }), candidates);
    }

    private static JevAnswer Choice(string value, params (string Key, double P)[] probabilities)
        => new("choice", value, probabilities.ToDictionary(static p => p.Key, static p => p.P), probabilities.Max(static p => p.P));

    private static JevAnswer Noul(double p) => new("noul", p >= .5 ? "true" : "false", new Dictionary<string, double> { ["noul"] = p }, p);

    private static BrowserGoal Goal(string? entity = null, SemanticEndState end = SemanticEndState.ResourceOpened)
        => BrowserGoal.FromUtterance("open it") with
        {
            Normalization = new("open the item", entity, null, null, null, [], "hint", [], end)
        };

    private static InteractionDecisionContext Context(BrowserGoal goal, InteractionObservation observation, ProofFamily family = ProofFamily.Activate)
        => new(new("open it", new OutcomeStep("s1", family, new("open the item"))), observation, [], new(), new(0, 0, 0, 0));

    private sealed class Gateway(Func<IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public IReadOnlyDictionary<string, JevQuestionDto>? Questions { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> questions,
            CancellationToken cancellationToken = default)
        {
            Questions = questions;
            return Task.FromResult(answer(questions));
        }
    }

    private static IReadOnlyDictionary<string, JevAnswer> ClickE1(JevAnswer? bind)
    {
        var answers = new Dictionary<string, JevAnswer>
        {
            ["operation"] = Choice("CLICK", ("CLICK", .9), ("DONE", .1)),
            ["click_target"] = Choice("e1", ("e1", .8), ("e2", .2)),
            ["goal_achieved"] = Noul(.1), ["stuck"] = Noul(.05)
        };
        if (bind is not null) answers["bind"] = bind;
        return answers;
    }

    [Fact]
    public async Task BindHead_IsAskedOnlyForActivateSteps_WhenEnabled()
    {
        var gateway = new Gateway(_ => ClickE1(null));
        var obs = Observation(("e1", "Docs"), ("e2", "Blog"));
        await new TypeSafeBrowserDecisionSource(gateway, Goal(), bindHead: true).DecideAsync(Context(Goal(), obs));
        Assert.Contains("bind", gateway.Questions!.Keys);
        Assert.Contains("NONE", gateway.Questions["bind"].Criteria!.Keys);
        Assert.Equal(["e1", "e2", "NONE"], gateway.Questions["bind"].Criteria!.Keys.Order());
        Assert.Contains("open the item", gateway.Questions["bind"].Instructions);

        await new TypeSafeBrowserDecisionSource(gateway, Goal(), bindHead: true).DecideAsync(Context(Goal(), obs, ProofFamily.Find));
        Assert.DoesNotContain("bind", gateway.Questions!.Keys);
        await new TypeSafeBrowserDecisionSource(gateway, Goal(), bindHead: false).DecideAsync(Context(Goal(), obs));
        Assert.DoesNotContain("bind", gateway.Questions!.Keys);
        var noStep = new InteractionDecisionContext(new("open it"), obs, [], new(), new(0, 0, 0, 0));
        await new TypeSafeBrowserDecisionSource(gateway, Goal(), bindHead: true).DecideAsync(noStep);
        Assert.DoesNotContain("bind", gateway.Questions!.Keys);
    }

    [Fact]
    public async Task JevBinding_CarriesTargetRevisionProbabilityAndMargin_WithoutChangingTheAction()
    {
        var gateway = new Gateway(_ => ClickE1(Choice("e2", ("e2", .9), ("e1", .06), ("NONE", .04))));
        var decision = await new TypeSafeBrowserDecisionSource(gateway, Goal(), bindHead: true)
            .DecideAsync(Context(Goal(), Observation(("e1", "Docs"), ("e2", "Blog"))));
        Assert.Equal("e1", decision.Action!.TargetId);            // the legacy choice is untouched
        var binding = decision.TargetBinding!;
        Assert.Equal(("e2", 1L, BindingMethod.JevChoice, 2), (binding.ElementRef, binding.ObservationRevision, binding.Method, binding.CandidateCount));
        Assert.Equal(.9, binding.P!.Value, 3);
        Assert.Equal(.84, binding.Margin!.Value, 3);
    }

    [Fact]
    public async Task NoneOrMissingBindAnswer_MeansNoBinding()
    {
        var obs = Observation(("e1", "Docs"), ("e2", "Blog"));
        var none = new Gateway(_ => ClickE1(Choice("NONE", ("NONE", .95), ("e1", .05))));
        Assert.Null((await new TypeSafeBrowserDecisionSource(none, Goal(), bindHead: true).DecideAsync(Context(Goal(), obs))).TargetBinding);
        var missing = new Gateway(_ => ClickE1(null));
        Assert.Null((await new TypeSafeBrowserDecisionSource(missing, Goal(), bindHead: true).DecideAsync(Context(Goal(), obs))).TargetBinding);
    }

    [Theory]
    [InlineData("Docs", "Docs", "e1", true)]
    [InlineData("the docs link", "Docs", "e1", true)]
    [InlineData("Docs", "docs ", "e1", true)]
    public async Task ExactLabel_BindsAUniqueExactAccessibleName(string entity, string name, string expectedRef, bool exact)
    {
        var gateway = new Gateway(_ => ClickE1(Choice("NONE", ("NONE", 1))));
        var decision = await new TypeSafeBrowserDecisionSource(gateway, Goal(entity), bindHead: true)
            .DecideAsync(Context(Goal(entity), Observation(("e1", name), ("e2", "Blog"))));
        Assert.Equal(exact, decision.TargetBinding?.Method == BindingMethod.ExactLabel);
        Assert.Equal(expectedRef, decision.TargetBinding?.ElementRef);
    }

    [Fact]
    public async Task ExactLabel_IsNotUsedForPartialOrDuplicateNames()
    {
        var gateway = new Gateway(_ => ClickE1(Choice("NONE", ("NONE", 1))));
        var partial = await new TypeSafeBrowserDecisionSource(gateway, Goal("Docs"), bindHead: true)
            .DecideAsync(Context(Goal("Docs"), Observation(("e1", "Docs and guides"), ("e2", "Blog"))));
        Assert.Null(partial.TargetBinding);
        var duplicate = await new TypeSafeBrowserDecisionSource(gateway, Goal("Docs"), bindHead: true)
            .DecideAsync(Context(Goal("Docs"), Observation(("e1", "Docs"), ("e2", "Docs"))));
        Assert.Null(duplicate.TargetBinding);
    }
}
