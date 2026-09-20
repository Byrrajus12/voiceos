using VoiceOS.Core.Speech;
using Xunit;

namespace VoiceOS.Core.Tests.Speech;

public class PcmToFloatConversionTests
{
    [Fact]
    public void EmptyInput_ReturnsEmptyArray()
    {
        var result = PcmToFloatConverter.Convert16BitPcmToFloat([]);
        Assert.Empty(result);
    }

    [Fact]
    public void ZeroSample_ConvertsToZeroFloat()
    {
        // 16-bit zero = 0x0000 LE
        byte[] pcm = [0x00, 0x00];
        var result = PcmToFloatConverter.Convert16BitPcmToFloat(pcm);
        Assert.Single(result);
        Assert.Equal(0f, result[0]);
    }

    [Fact]
    public void MaxPositiveSample_ConvertsToNearOne()
    {
        // Int16.MaxValue = 32767 = 0x7FFF LE
        byte[] pcm = [0xFF, 0x7F];
        var result = PcmToFloatConverter.Convert16BitPcmToFloat(pcm);
        Assert.Single(result);
        Assert.InRange(result[0], 0.99f, 1.0f);
    }

    [Fact]
    public void MaxNegativeSample_ConvertsToNegativeOne()
    {
        // Int16.MinValue = -32768 = 0x8000 LE
        byte[] pcm = [0x00, 0x80];
        var result = PcmToFloatConverter.Convert16BitPcmToFloat(pcm);
        Assert.Single(result);
        Assert.Equal(-1f, result[0]);
    }

    [Fact]
    public void TwoSamples_ConvertsCorrectly()
    {
        // First sample: 16384 (0x4000 LE) → 0.5f; Second: -16384 (0xC000 LE) → -0.5f
        byte[] pcm = [0x00, 0x40, 0x00, 0xC0];
        var result = PcmToFloatConverter.Convert16BitPcmToFloat(pcm);
        Assert.Equal(2, result.Length);
        Assert.InRange(result[0], 0.499f, 0.501f);
        Assert.InRange(result[1], -0.501f, -0.499f);
    }

    [Fact]
    public void OutputLengthIsHalfInputLength()
    {
        var pcm = new byte[100];
        var result = PcmToFloatConverter.Convert16BitPcmToFloat(pcm);
        Assert.Equal(50, result.Length);
    }
}
