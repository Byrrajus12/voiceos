using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public sealed class RunMapperTests
{
    private static TabInfo Tab(int id) => new(id, 1, false, $"https://e.com/{id}", "", BrowserTabProvenance.User, null, null);

    private static StateSnapshot Tabs(bool connected, params int[] ids)
        => new(DateTimeOffset.UtcNow, null, [], connected, ids.Select(Tab).ToArray(), null, 1);

    [Fact]
    public void NewTabs_AreTabIdsPresentAfterButNotBefore()
        => Assert.Equal(1, RunMapper.CountNewTabs(Tabs(true, 1, 2), Tabs(true, 2, 3)));

    [Fact]
    public void NewTabs_AreUnknownWithoutCompanionOnBothProbes()
        => Assert.Equal(0, RunMapper.CountNewTabs(Tabs(false), Tabs(true, 1, 2)));

    [Fact]
    public void AttemptedSteps_CarryTypedOperands_AndSkipUnattemptedSteps()
    {
        var program = new VoiceProgram([
            new MediaControlStep("s1", MediaOperation.Previous),
            new AdjustVolumeStep("s2", VolumeDirection.Down, 10),
            new SetVolumeStep("s3", 35)
        ]);

        var attempted = RunMapper.AttemptedSteps(program, ["s1", "s2"]);

        Assert.Equal([
            new AttemptedDirectStep("s1", "MediaControl", MediaOperation: MediaOperation.Previous),
            new AttemptedDirectStep("s2", "AdjustVolume", VolumeDirection: VolumeDirection.Down, VolumeAmount: 10)
        ], attempted);
    }
}
