using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ProofUserDetailTests
{
    [Theory]
    [InlineData("proof:link_followed_exact", ProofFamily.Activate, "The requested page is open.")]
    [InlineData("proof:surface_acquired_on_destination", ProofFamily.Surface, "The requested site is open.")]
    [InlineData("proof:query_results_shown", ProofFamily.Find, "The search results are showing.")]
    [InlineData("proof_refuted:query_mismatch", ProofFamily.Find, "The results shown do not match what was searched for.")]
    [InlineData("proof_refuted:wrong_destination", ProofFamily.Activate, "The page that opened is not the one that was requested.")]
    [InlineData("There is no earlier page in this tab.", ProofFamily.Reach, "There is no earlier page in this tab.")]
    [InlineData("The whole browser goal is confirmed on a fresh observation.", ProofFamily.Activate, "The whole browser goal is confirmed on a fresh observation.")]
    public void RuleIdsNeverReachTheUser(string detail, ProofFamily family, string expected)
        => Assert.Equal(expected, BrowserInteractionService.UserDetail(detail, family));

    [Fact]
    public void NullDetailStaysNull() => Assert.Null(BrowserInteractionService.UserDetail(null, ProofFamily.Activate));
}
