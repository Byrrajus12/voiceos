using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class SemanticGroupingTests
{
    private static InteractionObservation Observe(params (string Id, string Name, string Href, string Context)[] items)
        => new(1, "k", System.Text.Json.JsonSerializer.Serialize(new
        {
            current_url = "https://site.example/results", current_title = "t", visible_text = "",
            elements = items.Select(i => new { id = i.Id, Role = "link", Name = i.Name, Href = i.Href, Context = i.Context, Enabled = true })
        }), []);

    private static readonly (string, string, string, string)[] Runaway =
    [
        ("e57", "AURORA - Runaway (Official Video)", "https://site.example/watch?v=aurora", ""),
        ("e56", "4:10 Now playing", "https://site.example/watch?v=aurora", ""),                 // the duration-adjacent handle of the same result
        ("e61", "Kanye West - Runaway (Video Version)", "https://site.example/watch?v=kanye", ""),
        ("e70", "Subscribe", "https://site.example/subscribe", "")                               // an unrelated control
    ];

    [Fact]
    public void TwoHandlesOfOneResult_AreOneSemanticCandidate_AndAskNothingOnTheirOwn()
    {
        var obs = Observe(Runaway[0], Runaway[1], Runaway[3]);
        var verdict = TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e57"] = .49, ["e56"] = .41, ["e70"] = .02, ["NONE"] = .08 },
            ["e57", "e56", "e70"], "a matching \"Runaway\" video", "Play Runaway on YouTube");
        Assert.Equal(TargetAmbiguity.Kind.Winner, verdict.Kind);
        Assert.Equal("same_item", verdict.Why);
        Assert.Equal("e57", verdict.Ref);
    }

    [Fact]
    public void ADistinctResultWithTheRequestedTitle_SurvivesEvenWhenTheBinderGaveItNoMass_AndIsAskedAbout()
    {
        var obs = Observe(Runaway);
        // The binder spent everything on the two AURORA handles; Kanye West's result is a different item that carries the user's word.
        var verdict = TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e57"] = .49, ["e56"] = .41, ["e61"] = .01, ["e70"] = .01, ["NONE"] = .08 },
            ["e57", "e56", "e61", "e70"], "a matching \"Runaway\" video", "Play Runaway on YouTube");
        Assert.Equal(TargetAmbiguity.Kind.Ask, verdict.Kind);
        Assert.Equal(["AURORA - Runaway (Official Video)", "Kanye West - Runaway (Video Version)"], verdict.Options!.Select(o => o.DisplayText));
        Assert.Contains("group1=\"AURORA - Runaway (Official Video)\" handles=[e57,e56]", verdict.Summary);
        Assert.Contains("group2=\"Kanye West - Runaway (Video Version)\"", verdict.Summary);
    }

    [Fact]
    public void WhenTheUserNamedTheArtist_TheOtherResultIsNotAContender()
    {
        var obs = Observe(Runaway);
        var verdict = TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e57"] = .49, ["e56"] = .41, ["e61"] = .01, ["NONE"] = .08 },
            ["e57", "e56", "e61"], "Runaway by Kanye West", "Play Runaway by Kanye West");
        Assert.Equal(TargetAmbiguity.Kind.Winner, verdict.Kind);
        Assert.Equal("e61", verdict.Ref);
    }

    // -- a group wins only with evidence connecting it to the request -------------------------------------------

    [Fact]
    public void OneGroupTheBinderLikes_ButThatCarriesNoneOfTheRequestedWords_IsNotAWinner()
    {
        var obs = Observe(("e66", "Don Toliver - No Idea [Official Music Video]", "https://site.example/watch?v=t", ""),
            ("e67", "4:10 Now playing", "https://site.example/watch?v=t", ""), ("e70", "Subscribe", "https://site.example/s", ""));
        var verdict = TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e66"] = .35, ["e67"] = .30, ["e70"] = .02, ["NONE"] = .1 },
            ["e66", "e67", "e70"], "a video result for \"hello\"", "Play Hello on YouTube");
        Assert.Equal(TargetAmbiguity.Kind.Unresolvable, verdict.Kind);
        Assert.Equal("no_target_evidence", verdict.Why);
    }

    [Fact]
    public void OneGroupThatCarriesTheRequestedWords_IsAWinner()
    {
        var obs = Observe(("e1", "Hello - Adele (Official Video)", "https://site.example/watch?v=a", ""), ("e2", "6:07", "https://site.example/watch?v=a", ""),
            ("e3", "Hello - Lionel Richie", "https://site.example/watch?v=b", ""));
        var verdict = TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e1"] = .35, ["e2"] = .30, ["e3"] = .01, ["NONE"] = .1 },
            ["e1", "e2", "e3"], "Hello by Adele", "Play Hello by Adele");
        Assert.Equal(TargetAmbiguity.Kind.Winner, verdict.Kind);
        Assert.Equal("e1", verdict.Ref);
    }

    [Fact]
    public void ControlsThatAreOneResult_AreOneOption_AndTimestampsAreNeverOffered()
    {
        var obs = Observe(("e1", "Hello", "https://site.example/watch?v=a", "Adele"),
            ("e2", "6:07", "https://site.example/watch?v=a", "Adele"),          // the duration badge over the same result
            ("e3", "Hello (Live)", "https://site.example/watch?v=a#t=3", "Adele"),   // another handle on it
            ("e4", "Hello", "https://site.example/watch?v=b", "Lionel Richie"));
        var probabilities = new Dictionary<string, double> { ["e1"] = .22, ["e2"] = .14, ["e3"] = .14, ["e4"] = .44, ["NONE"] = .06 };

        var verdict = TargetAmbiguity.Assess(obs, probabilities, ["e1", "e2", "e3", "e4"], "Hello", "Play hello");

        Assert.Equal(TargetAmbiguity.Kind.Ask, verdict.Kind);
        Assert.Equal(2, verdict.Options!.Count);
        Assert.DoesNotContain(verdict.Options, o => o.DisplayText == "6:07");
        Assert.Equal(["Adele", "Lionel Richie"], verdict.Options.Select(o => o.SecondaryText));
    }

    [Fact]
    public void AnOrdinalOrAClearLead_IsNotAQuestion()
    {
        var obs = Observe(("e1", "Hello", "https://site.example/a", "Adele"), ("e2", "Goodbye", "https://site.example/b", "Lionel Richie"));
        var close = new Dictionary<string, double> { ["e1"] = .47, ["e2"] = .43, ["NONE"] = .1 };
        Assert.Equal(TargetAmbiguity.Kind.Clear, TargetAmbiguity.Assess(obs, new Dictionary<string, double> { ["e1"] = .8, ["e2"] = .15, ["NONE"] = .05 },
            ["e1", "e2"], "Hello", "Play hello").Kind);
        // "the second result" is a position the user named; it is only a winner when the page reports positions, and never asks.
        Assert.NotEqual(TargetAmbiguity.Kind.Ask, TargetAmbiguity.Assess(obs, close, ["e1", "e2"], "the second result", "Open the second result").Kind);
    }
}
