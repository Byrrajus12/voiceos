using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

public class VolumeExtractorTests
{
    // ── TryExtractPercent: digit forms ────────────────────────────────────────

    [Theory]
    [InlineData("set volume to 15", 15)]
    [InlineData("Set volume to 15.", 15)]          // live transcript form with punctuation
    [InlineData("volume 50", 50)]
    [InlineData("set volume to 0", 0)]
    [InlineData("make it 100 percent", 100)]
    [InlineData("volume at 80", 80)]
    [InlineData("set it to 30 percent", 30)]
    [InlineData("volume at 50 percent", 50)]
    public void ExtractPercent_DigitForms_Correct(string transcript, int expected)
    {
        Assert.True(VolumeExtractor.TryExtractPercent(transcript, out int pct));
        Assert.Equal(expected, pct);
    }

    // ── TryExtractPercent: word-number forms (with and without terminal punctuation) ──

    [Theory]
    [InlineData("set volume to zero", 0)]
    [InlineData("set volume to zero.", 0)]        // live transcript form
    [InlineData("set volume to fifteen", 15)]
    [InlineData("Set volume to fifteen.", 15)]     // exact live transcript
    [InlineData("Set volume to fifty.", 50)]       // exact live transcript
    [InlineData("volume fifty", 50)]
    [InlineData("set volume to one hundred", 100)]
    [InlineData("set volume to one hundred.", 100)]
    [InlineData("make it thirty", 30)]
    [InlineData("volume at twenty", 20)]
    [InlineData("set it to ninety", 90)]
    [InlineData("set volume to twenty five", 25)]
    [InlineData("set volume to thirty five", 35)]
    [InlineData("set volume to ninety five", 95)]
    public void ExtractPercent_WordNumberForms_Correct(string transcript, int expected)
    {
        Assert.True(VolumeExtractor.TryExtractPercent(transcript, out int pct));
        Assert.Equal(expected, pct);
    }

    // ── TryExtractPercent: clamping ───────────────────────────────────────────

    [Theory]
    [InlineData("set volume to 150", 100)]   // digit over 100
    [InlineData("set volume to 200", 100)]
    public void ExtractPercent_DigitOverRange_ClampedTo100(string transcript, int expected)
    {
        Assert.True(VolumeExtractor.TryExtractPercent(transcript, out int pct));
        Assert.Equal(expected, pct);
    }

    [Fact]
    public void ExtractPercent_Zero_ClampedToZero()
    {
        Assert.True(VolumeExtractor.TryExtractPercent("set volume to 0", out int pct));
        Assert.Equal(0, pct);
    }

    // ── TryExtractPercent: no number present ──────────────────────────────────

    [Theory]
    [InlineData("turn it up")]
    [InlineData("increase the volume")]
    [InlineData("")]
    [InlineData("volume")]
    public void ExtractPercent_NoNumber_ReturnsFalse(string transcript)
    {
        Assert.False(VolumeExtractor.TryExtractPercent(transcript, out _));
    }

}
