using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

public class VoicePlanTests
{
    [Fact]
    public void None_HasNoneAction()
    {
        var plan = new VoicePlan(VoiceAction.None);
        Assert.Equal(VoiceAction.None, plan.Action);
        Assert.Equal(0.0, plan.Confidence);
        Assert.False(plan.RequiresClarification);
    }

    [Fact]
    public void Rejected_HasRejectedAction()
    {
        var plan = new VoicePlan(VoiceAction.Rejected, RejectionReason: "low confidence");
        Assert.Equal(VoiceAction.Rejected, plan.Action);
        Assert.Equal("low confidence", plan.RejectionReason);
    }

    [Fact]
    public void OpenApp_StoresAppCandidate()
    {
        var plan = new VoicePlan(VoiceAction.OpenApp, AppCandidate: "Google Chrome", Confidence: 0.9);
        Assert.Equal(VoiceAction.OpenApp, plan.Action);
        Assert.Equal("Google Chrome", plan.AppCandidate);
        Assert.Equal(0.9, plan.Confidence);
    }

    [Fact]
    public void MediaControl_StoresMediaOp()
    {
        var plan = new VoicePlan(VoiceAction.MediaControl, Media: MediaOperation.Next, Confidence: 0.8);
        Assert.Equal(VoiceAction.MediaControl, plan.Action);
        Assert.Equal(MediaOperation.Next, plan.Media);
    }

    [Fact]
    public void SnapCurrentWindow_StoresDirection()
    {
        var plan = new VoicePlan(VoiceAction.SnapCurrentWindow, Snap: SnapDirection.Left, Confidence: 0.75);
        Assert.Equal(VoiceAction.SnapCurrentWindow, plan.Action);
        Assert.Equal(SnapDirection.Left, plan.Snap);
    }

    [Fact]
    public void SetVolume_StoresVolumeValue()
    {
        var plan = new VoicePlan(VoiceAction.SetVolume, VolumeValue: 30, Confidence: 0.85);
        Assert.Equal(VoiceAction.SetVolume, plan.Action);
        Assert.Equal(30, plan.VolumeValue);
    }

    [Fact]
    public void AdjustVolume_StoresDirection()
    {
        var plan = new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: VolumeDirection.Up, Confidence: 0.7);
        Assert.Equal(VoiceAction.AdjustVolume, plan.Action);
        Assert.Equal(VolumeDirection.Up, plan.VolumeAdjust);
    }

    [Fact]
    public void RequiresClarification_IsStoredCorrectly()
    {
        var plan = new VoicePlan(VoiceAction.FocusWindow, RequiresClarification: true, Confidence: 0.45);
        Assert.True(plan.RequiresClarification);
    }

    [Theory]
    [InlineData(VoiceAction.CloseCurrentWindow)]
    [InlineData(VoiceAction.MaximizeCurrentWindow)]
    [InlineData(VoiceAction.MinimizeCurrentWindow)]
    public void WindowActions_HaveNullCandidates(VoiceAction action)
    {
        var plan = new VoicePlan(action, Confidence: 0.9);
        Assert.Null(plan.AppCandidate);
        Assert.Null(plan.WindowCandidate);
    }
}
