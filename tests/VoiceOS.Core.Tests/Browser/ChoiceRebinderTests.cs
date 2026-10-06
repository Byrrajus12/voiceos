using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ChoiceRebinderTests
{
    // -- rebinding a chosen option ------------------------------------------------------------------------------

    private static EvidenceElement El(string id, string name, string? href, string? context = null, int? position = null)
        => new(id, "link", name, href, null, false, true, true, null, null, null, Position: position, Context: context);

    private static RebindResult Rebind(ChoiceOption option, params EvidenceElement[] page)
        => ChoiceRebinder.Find(option, page, _ => true);

    private static ChoiceOption Option(string label, string? href, string? context = null, int? position = 3)
        => new("c1", "e9", label, null, .5, href, "link", context, position, ["4:10 Now playing"]);

    [Fact]
    public void AChoice_SurvivesRefChurnAndListMovement_WhenTheCanonicalAddressRemains()
    {
        var option = Option("AURORA - Runaway", "https://site.example/watch?v=aurora&utm_source=feed#t=1");
        var found = Rebind(option,
            El("x1", "Kanye West - Runaway", "https://site.example/watch?v=kanye", position: 1),
            El("x7", "4:10 Now playing", "https://site.example/watch?v=aurora", position: 7),          // moved from 3 to 7, other handle only
            El("x8", "AURORA - Runaway (Official)", "https://site.example/watch?v=aurora/", position: 7));
        Assert.Equal(RebindKind.Found, found.Kind);
        Assert.Equal("address", found.Via);
        Assert.StartsWith("x", found.ElementId);
        Assert.Contains(found.ElementId, new[] { "x7", "x8" });
    }

    [Fact]
    public void AChoice_SurvivesAChangedAddress_WhenTheLabelAndContainerRemainUnique()
    {
        var option = Option("Hello", "https://site.example/watch?v=a", "Adele");
        var found = Rebind(option, El("n1", "Hello", "https://site.example/watch?v=a&pp=xyz", "Adele"), El("n2", "Hello", "https://site.example/watch?v=b", "Lionel Richie"));
        Assert.Equal(RebindKind.Found, found.Kind);
        Assert.Equal("n1", found.ElementId);
        Assert.Equal("label_container", found.Via);
    }

    [Fact]
    public void ARebindThatStillMatchesSeveralResults_FailsSafely_InsteadOfChoosing()
    {
        var option = Option("Hello", "https://site.example/old", "Adele");
        var found = Rebind(option, El("n1", "Hello", "https://site.example/x", "Adele"), El("n2", "Hello", "https://site.example/y", "Adele"));
        Assert.Equal(RebindKind.Ambiguous, found.Kind);
        Assert.Null(found.ElementId);
    }

    [Fact]
    public void ASelectedItemThatIsReallyGone_IsMissing()
    {
        var option = Option("Hello", "https://site.example/old", "Adele");
        var found = Rebind(option, El("n1", "Goodbye", "https://site.example/x", "Adele"), El("n2", "Hello", "https://site.example/y", "Lionel Richie"));
        Assert.Equal(RebindKind.Missing, found.Kind);
    }
}
