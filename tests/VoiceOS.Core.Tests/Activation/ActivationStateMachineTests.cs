using VoiceOS.Core.Activation;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public class ActivationStateMachineTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (MachineContext Next, IReadOnlyList<ActivationAction> Actions) Step(
        MachineContext ctx, ActivationEvent evt, ActivationMode mode, DateTimeOffset now)
        => ActivationStateMachine.Transition(ctx, evt, mode, Grace, Hold, now);

    // ─── PushToTalk ─────────────────────────────────────────────────────────────

    [Fact]
    public void PTT_Idle_KeyDown_StartsRecording()
    {
        var (next, actions) = Step(MachineContext.Initial, ActivationEvent.KeyDown, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Single(actions, a => a is ActivationAction.StartRecording);
    }

    [Fact]
    public void PTT_Recording_KeyUp_StartsGraceTimer()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Recording };
        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Stopping, next.State);
        Assert.Single(actions, a => a is ActivationAction.StartGraceTimer t && t.Duration == Grace);
    }

    [Fact]
    public void PTT_Stopping_GraceExpired_StopsRecording()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Stopping };
        var (next, actions) = Step(ctx, ActivationEvent.GraceTimerExpired, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.Single(actions, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void PTT_Stopping_KeyDown_CancelsGraceAndResumesRecording()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Stopping };
        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Single(actions, a => a is ActivationAction.CancelGraceTimer);
        Assert.DoesNotContain(actions, a => a is ActivationAction.StopRecording);
        Assert.DoesNotContain(actions, a => a is ActivationAction.StartRecording);
    }

    [Fact]
    public void PTT_Idle_KeyUp_IsNoOp()
    {
        var (next, actions) = Step(MachineContext.Initial, ActivationEvent.KeyUp, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.Empty(actions);
    }

    [Fact]
    public void PTT_Idle_GraceExpired_IsNoOp()
    {
        var (next, actions) = Step(MachineContext.Initial, ActivationEvent.GraceTimerExpired, ActivationMode.PushToTalk, T0);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.Empty(actions);
    }

    // ─── Toggle ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_Idle_KeyDown_StartsRecording()
    {
        var (next, actions) = Step(MachineContext.Initial, ActivationEvent.KeyDown, ActivationMode.Toggle, T0);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Single(actions, a => a is ActivationAction.StartRecording);
    }

    [Fact]
    public void Toggle_Recording_KeyDown_StopsRecording()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Recording };
        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.Toggle, T0);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.Single(actions, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void Toggle_Recording_KeyUp_IsNoOp()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Recording };
        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.Toggle, T0);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Empty(actions);
    }

    // ─── HoldOrToggle — tap path (quick press < holdThreshold) ─────────────────

    [Fact]
    public void HOT_Idle_KeyDown_StartsRecordingInHeldMode()
    {
        var (next, actions) = Step(MachineContext.Initial, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, T0);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.False(next.IsToggled);
        Assert.Equal(T0, next.PressedAt);
        Assert.Single(actions, a => a is ActivationAction.StartRecording);
    }

    [Fact]
    public void HOT_Recording_QuickKeyUp_SwitchesToToggledMode()
    {
        // KeyDown at T0, KeyUp 100ms later (< 300ms threshold) → toggle on
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: false, PressedAt: T0);
        var tUp = T0.AddMilliseconds(100);

        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, tUp);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.True(next.IsToggled);
        Assert.Empty(actions); // No StartRecording/StopRecording — continues recording
    }

    [Fact]
    public void HOT_RecordingToggled_KeyDown_StopsRecording()
    {
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: true, PressedAt: T0);
        var tStop = T0.AddSeconds(2);

        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, tStop);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.False(next.IsToggled);
        Assert.Single(actions, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void HOT_RecordingToggled_KeyUp_IsNoOp()
    {
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: true, PressedAt: T0);
        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, T0.AddSeconds(1));

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.True(next.IsToggled);
        Assert.Empty(actions);
    }

    // ─── HoldOrToggle — hold path (press ≥ holdThreshold) ─────────────────────

    [Fact]
    public void HOT_Recording_LongKeyUp_StartsGraceTimer()
    {
        // KeyDown at T0, KeyUp 400ms later (≥ 300ms threshold) → PTT grace
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: false, PressedAt: T0);
        var tUp = T0.AddMilliseconds(400);

        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, tUp);

        Assert.Equal(ActivationState.Stopping, next.State);
        Assert.Single(actions, a => a is ActivationAction.StartGraceTimer t && t.Duration == Grace);
    }

    [Fact]
    public void HOT_Recording_KeyUpAtExactThreshold_IsHold()
    {
        // At exactly the threshold, it should be treated as a hold (duration is NOT < threshold)
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: false, PressedAt: T0);
        var tUp = T0 + Hold; // exactly 300ms

        var (next, actions) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, tUp);

        Assert.Equal(ActivationState.Stopping, next.State);
        Assert.Single(actions, a => a is ActivationAction.StartGraceTimer);
    }

    [Fact]
    public void HOT_Stopping_GraceExpired_StopsRecording()
    {
        var ctx = MachineContext.Initial with { State = ActivationState.Stopping };
        var (next, actions) = Step(ctx, ActivationEvent.GraceTimerExpired, ActivationMode.HoldOrToggle, T0);

        Assert.Equal(ActivationState.Idle, next.State);
        Assert.Single(actions, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void HOT_Stopping_KeyDown_CancelsGraceAndResumesHeldRecording()
    {
        var ctx = new MachineContext(ActivationState.Stopping, IsToggled: false, PressedAt: T0);
        var tRepress = T0.AddMilliseconds(80);

        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, tRepress);

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.False(next.IsToggled);
        Assert.Equal(tRepress, next.PressedAt);
        Assert.Single(actions, a => a is ActivationAction.CancelGraceTimer);
        Assert.DoesNotContain(actions, a => a is ActivationAction.StopRecording);
    }

    // ─── Full round-trip sequences ───────────────────────────────────────────────

    [Fact]
    public void PTT_FullCycle_IdleToRecordingToIdle()
    {
        var ctx = MachineContext.Initial;

        (ctx, var a1) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.PushToTalk, T0);
        Assert.Equal(ActivationState.Recording, ctx.State);
        Assert.Contains(a1, a => a is ActivationAction.StartRecording);

        var tUp = T0.AddSeconds(1);
        (ctx, var a2) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.PushToTalk, tUp);
        Assert.Equal(ActivationState.Stopping, ctx.State);
        Assert.Contains(a2, a => a is ActivationAction.StartGraceTimer);

        (ctx, var a3) = Step(ctx, ActivationEvent.GraceTimerExpired, ActivationMode.PushToTalk, tUp.AddMilliseconds(150));
        Assert.Equal(ActivationState.Idle, ctx.State);
        Assert.Contains(a3, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void HOT_TapThenToggleOff_FullCycle()
    {
        var ctx = MachineContext.Initial;

        // Press
        (ctx, _) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, T0);
        Assert.Equal(ActivationState.Recording, ctx.State);
        Assert.False(ctx.IsToggled);

        // Quick release → toggled on
        (ctx, var a2) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, T0.AddMilliseconds(100));
        Assert.Equal(ActivationState.Recording, ctx.State);
        Assert.True(ctx.IsToggled);
        Assert.Empty(a2);

        // Second press → stop
        (ctx, var a3) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, T0.AddSeconds(3));
        Assert.Equal(ActivationState.Idle, ctx.State);
        Assert.Contains(a3, a => a is ActivationAction.StopRecording);
    }

    [Fact]
    public void HOT_HoldThenRelease_FullCycle()
    {
        var ctx = MachineContext.Initial;

        (ctx, _) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, T0);

        // Long release → grace period
        (ctx, var a2) = Step(ctx, ActivationEvent.KeyUp, ActivationMode.HoldOrToggle, T0.AddMilliseconds(500));
        Assert.Equal(ActivationState.Stopping, ctx.State);
        Assert.Contains(a2, a => a is ActivationAction.StartGraceTimer);

        // Grace expires → stop
        (ctx, var a3) = Step(ctx, ActivationEvent.GraceTimerExpired, ActivationMode.HoldOrToggle, T0.AddMilliseconds(650));
        Assert.Equal(ActivationState.Idle, ctx.State);
        Assert.Contains(a3, a => a is ActivationAction.StopRecording);
    }

    // ─── Key-repeat / debounce ──────────────────────────────────────────────────
    // The state machine receives only clean edge events; the hook layer suppresses
    // repeats by tracking _keyIsDown. The machine's own guard is that a second
    // KeyDown while already Recording (non-toggled) is a no-op.

    [Fact]
    public void PTT_RecordingKeyDown_InRecordingState_IsNoOp()
    {
        // Simulates a spurious second KeyDown reaching the machine (should not happen with
        // hook-level filtering, but the machine must handle it gracefully).
        var ctx = new MachineContext(ActivationState.Recording);
        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.PushToTalk, T0);

        // No mode-level handler for Recording+KeyDown in PTT → falls through to default no-op
        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Empty(actions);
    }

    [Fact]
    public void HOT_RecordingHeld_KeyDown_IsNoOp()
    {
        // In held (non-toggled) Recording, a second KeyDown is a no-op.
        var ctx = new MachineContext(ActivationState.Recording, IsToggled: false, PressedAt: T0);
        var (next, actions) = Step(ctx, ActivationEvent.KeyDown, ActivationMode.HoldOrToggle, T0.AddMilliseconds(10));

        Assert.Equal(ActivationState.Recording, next.State);
        Assert.Empty(actions);
    }
}
