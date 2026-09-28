using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public class LiveEvalOptionsTests
{
    [Fact]
    public void ParsesRepeatTagsAndFlags()
    {
        var options = LiveEvalOptions.Parse(["--tag", "smoke", "--tag", "browser", "--repeat", "3",
            "--include-unsafe", "--verbose", "--all"]);
        Assert.Equal(LiveEvalMode.Run, options.Mode);
        Assert.Equal(["smoke", "browser"], options.Tags);
        Assert.Equal(3, options.Repeat);
        Assert.True(options.IncludeUnsafe);
        Assert.True(options.Verbose);
        Assert.True(options.All);
    }

    [Fact]
    public void SummarizeModeCollectsFilePaths()
    {
        var options = LiveEvalOptions.Parse(["summarize", "a.jsonl", "b.jsonl"]);
        Assert.Equal(LiveEvalMode.Summarize, options.Mode);
        Assert.Equal(["a.jsonl", "b.jsonl"], options.SummarizeFiles);
    }

    [Fact]
    public void SummarizeWithNoFilesThrows()
    {
        Assert.Throws<ArgumentException>(() => LiveEvalOptions.Parse(["summarize"]));
    }

    [Fact]
    public void UnknownFlagThrows()
    {
        Assert.Throws<ArgumentException>(() => LiveEvalOptions.Parse(["--bogus"]));
    }

    [Fact]
    public void RepeatBelowOneThrows()
    {
        Assert.Throws<ArgumentException>(() => LiveEvalOptions.Parse(["--all", "--repeat", "0"]));
    }
}
