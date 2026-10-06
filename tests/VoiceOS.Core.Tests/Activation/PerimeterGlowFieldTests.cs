using System.Drawing;
using VoiceOS.UI;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class PerimeterGlowFieldTests
{
    [Fact]
    public void CornersUseOneWrappedPerimeterCoordinate()
    {
        var field = new PerimeterGlowField(new Rectangle(-1280, 0, 1280, 800), 46);
        int perimeter = 2 * (1279 + 799);
        Assert.Equal(0, field.Locate(-1280, 0).PerimeterIndex);
        Assert.Equal(1279, field.Locate(-1, 0).PerimeterIndex);
        Assert.Equal(1279 + 799, field.Locate(-1, 799).PerimeterIndex);
        Assert.Equal(2 * 1279 + 799, field.Locate(-1280, 799).PerimeterIndex);

        // Samples approaching each corner from both incident edges remain adjacent,
        // including the wrap from the last left-edge sample to the first top sample.
        foreach (var (ax, ay, bx, by) in new[]
        {
            (-1279, 0, -1280, 1), (-2, 0, -1, 1),
            (-1, 798, -2, 799), (-1279, 799, -1280, 798)
        })
        {
            int a = field.Locate(ax, ay).PerimeterIndex;
            int b = field.Locate(bx, by).PerimeterIndex;
            int separation = Math.Abs(a - b);
            Assert.True(Math.Min(separation, perimeter - separation) <= 2);
        }
    }

    [Fact]
    public void SharedFieldContinuesAcrossStripJunctions()
    {
        const int edge = 46;
        var field = new PerimeterGlowField(new Rectangle(0, 0, 1280, 800), edge);
        foreach (int x in new[] { 0, 1, 5, 12 })
        {
            var above = field.Locate(x, edge - 1);
            var below = field.Locate(x, edge);
            Assert.Equal(x, above.Distance);
            Assert.Equal(x, below.Distance);
            int separation = Math.Abs(above.PerimeterIndex - below.PerimeterIndex);
            int perimeter = 2 * (1279 + 799);
            Assert.True(Math.Min(separation, perimeter - separation) <= 2);
        }
    }

    [Fact]
    public void AnimationPhaseWrapsWithoutChangingField()
    {
        var field = new PerimeterGlowField(new Rectangle(0, 0, 1280, 800), 46);
        field.Update(0.37, 0.35);
        int before = field.Pixel(0, 314);
        field.Update(0.37 + 2 * Math.PI, 0.35);
        Assert.Equal(before, field.Pixel(0, 314));
    }
}
