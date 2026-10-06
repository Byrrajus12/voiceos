using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// The repeat-click guard keyed on element identity from the effect ledger: two controls that share a visible
/// label are different elements; the very same element re-clicked on the same page is still a loop.
/// </summary>
public sealed class EffectAwareRepeatTests
{
    private const string Url = "https://shop.test/list";

    private static (string Role, string Name, string Context, string Href) El(string reference, string context, string href)
        => ("link", "Search", context, href);

    private static string Evidence(params (string Ref, string Context, string Href)[] links)
        => JsonSerializer.Serialize(new
        {
            current_url = Url, current_title = "List",
            elements = links.Select(l => new { id = l.Ref, Role = "link", Name = "Search", Value = "", Href = l.Href, Context = l.Context })
        });

    private static InteractionObservation Observation(params (string Ref, string Context, string Href)[] links)
        => new(2, "k2", Evidence(links), links.Select(l => new InteractionCandidate(l.Ref, "link 'Search'",
            [new($"r2:click:{l.Ref}", InteractionActionKind.Activate, l.Ref)])).ToArray());

    /// <summary>An earlier click on the element (context, href) from the same URL, plus the Activated effect that recorded it.</summary>
    private static (InteractionHistoryEntry Entry, Effect Effect) Earlier(string context, string href)
    {
        var action = new InteractionAction("r1:click:e9", InteractionActionKind.Activate, "e9");
        var entry = new InteractionHistoryEntry("k1", action, InteractionActionResult.Ok(), "k2", false, DateTimeOffset.UtcNow,
            Evidence(("e9", context, href)), Evidence(("e9", context, href)), "link 'Search'");
        var fingerprint = BrowserEffectEmitter.Fingerprint("link", "Search", context, href);
        var effect = new Effect(EffectKind.Activated, EffectSource.CompanionResponse, EffectStrength.Observed,
            new TypedRef(1, "s", 1, "e9", fingerprint, "Search", "link", href)) { ActionId = action.Id, StepId = "s1", Id = "s1.1" };
        return (entry, effect);
    }

    private sealed class Gateway(string target) : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> questions,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "CLICK", new Dictionary<string, double> { ["CLICK"] = .9, ["DONE"] = .1 }, .9),
                ["click_target"] = new("choice", target, new Dictionary<string, double> { [target] = .9 }, .9),
                ["goal_achieved"] = new("noul", "false", new Dictionary<string, double> { ["noul"] = .1 }, .1),
                ["stuck"] = new("noul", "false", new Dictionary<string, double> { ["noul"] = .05 }, .05)
            });
    }

    private static async Task<InteractionDecision> Decide(bool effectAware, string target, params (string Ref, string Context, string Href)[] links)
    {
        var (entry, effect) = Earlier("results header", "https://shop.test/search");
        var observation = Observation(links);
        var context = new InteractionDecisionContext(new("find it"), observation, [entry], new(), new(1, 1, 1, 0), [effect]);
        var goal = BrowserGoal.FromUtterance("find it");
        return await new TypeSafeBrowserDecisionSource(new Gateway(target), goal, effectAwareRepeat: effectAware).DecideAsync(context);
    }

    [Fact]
    public async Task SameLabelDifferentElement_IsNotARepeat_WhenEffectAware_ButIsInLegacy()
    {
        // e1 is the element clicked before; e2 shares its label but sits elsewhere and points elsewhere.
        var links = new[] { ("e1", "results header", "https://shop.test/search"), ("e2", "footer", "https://shop.test/help/search") };
        var aware = await Decide(true, "e2", links);
        Assert.NotNull(aware.Action);
        Assert.Equal("e2", aware.Action!.TargetId);
        var legacy = await Decide(false, "e2", links);
        Assert.Equal(InteractionCompletionState.Uncertain, legacy.Completion);
    }

    [Fact]
    public async Task TheSameElementAgain_IsStillARepeat_InBothModes()
    {
        var links = new[] { ("e1", "results header", "https://shop.test/search"), ("e2", "footer", "https://shop.test/help/search") };
        Assert.Equal(InteractionCompletionState.Uncertain, (await Decide(true, "e1", links)).Completion);
        Assert.Equal(InteractionCompletionState.Uncertain, (await Decide(false, "e1", links)).Completion);
    }

    [Fact]
    public async Task WithoutALedger_EffectAwareFallsBackToTheLabelRule()
    {
        var (entry, _) = Earlier("results header", "https://shop.test/search");
        var observation = Observation(("e2", "footer", "https://shop.test/help/search"));
        var context = new InteractionDecisionContext(new("find it"), observation, [entry], new(), new(1, 1, 1, 0));
        var decision = await new TypeSafeBrowserDecisionSource(new Gateway("e2"), BrowserGoal.FromUtterance("find it"), effectAwareRepeat: true)
            .DecideAsync(context);
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
    }
}
