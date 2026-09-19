namespace VoiceOS.Core.Activation;

/// <summary>
/// Pure state machine ported from Handy's transcription_coordinator.rs CoordinatorState.
/// All transitions return (new context, list of actions) with no side effects.
/// Actions are executed by the caller (ActivationOrchestrator).
/// </summary>
public static class ActivationStateMachine
{
    public static (MachineContext Next, IReadOnlyList<ActivationAction> Actions) Transition(
        MachineContext ctx,
        ActivationEvent evt,
        ActivationMode mode,
        TimeSpan gracePeriod,
        TimeSpan holdThreshold,
        DateTimeOffset now)
    {
        var actions = new List<ActivationAction>();
        var next = mode switch
        {
            ActivationMode.PushToTalk => TransitionPushToTalk(ctx, evt, actions, gracePeriod, now),
            ActivationMode.Toggle => TransitionToggle(ctx, evt, actions, now),
            ActivationMode.HoldOrToggle => TransitionHoldOrToggle(ctx, evt, actions, gracePeriod, holdThreshold, now),
            _ => ctx
        };
        return (next, actions);
    }

    private static MachineContext TransitionPushToTalk(
        MachineContext ctx, ActivationEvent evt, List<ActivationAction> actions,
        TimeSpan gracePeriod, DateTimeOffset now)
    {
        switch (ctx.State)
        {
            case ActivationState.Idle when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.StartRecording());
                return ctx with { State = ActivationState.Recording, PressedAt = now };

            case ActivationState.Recording when evt == ActivationEvent.KeyUp:
                actions.Add(new ActivationAction.StartGraceTimer(gracePeriod));
                return ctx with { State = ActivationState.Stopping };

            case ActivationState.Stopping when evt == ActivationEvent.GraceTimerExpired:
                actions.Add(new ActivationAction.StopRecording());
                return ctx with { State = ActivationState.Idle, PressedAt = null };

            // Re-press during grace period: cancel timer and resume recording.
            // Audio capture is still running so no StartRecording needed.
            case ActivationState.Stopping when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.CancelGraceTimer());
                return ctx with { State = ActivationState.Recording, PressedAt = now };

            default:
                return ctx;
        }
    }

    private static MachineContext TransitionToggle(
        MachineContext ctx, ActivationEvent evt, List<ActivationAction> actions,
        DateTimeOffset now)
    {
        switch (ctx.State)
        {
            case ActivationState.Idle when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.StartRecording());
                return ctx with { State = ActivationState.Recording, PressedAt = now };

            case ActivationState.Recording when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.StopRecording());
                return ctx with { State = ActivationState.Idle, PressedAt = null };

            default:
                return ctx;
        }
    }

    private static MachineContext TransitionHoldOrToggle(
        MachineContext ctx, ActivationEvent evt, List<ActivationAction> actions,
        TimeSpan gracePeriod, TimeSpan holdThreshold, DateTimeOffset now)
    {
        switch (ctx.State)
        {
            case ActivationState.Idle when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.StartRecording());
                return ctx with { State = ActivationState.Recording, IsToggled = false, PressedAt = now };

            case ActivationState.Recording when evt == ActivationEvent.KeyUp && !ctx.IsToggled:
                var duration = now - (ctx.PressedAt ?? now);
                if (duration < holdThreshold)
                {
                    // Quick tap: lock recording on until next press.
                    return ctx with { IsToggled = true };
                }
                else
                {
                    // Held long enough: PTT release → start grace period.
                    actions.Add(new ActivationAction.StartGraceTimer(gracePeriod));
                    return ctx with { State = ActivationState.Stopping };
                }

            case ActivationState.Recording when evt == ActivationEvent.KeyDown && ctx.IsToggled:
                // Second press ends the toggled recording.
                actions.Add(new ActivationAction.StopRecording());
                return ctx with { State = ActivationState.Idle, IsToggled = false, PressedAt = null };

            case ActivationState.Stopping when evt == ActivationEvent.GraceTimerExpired:
                actions.Add(new ActivationAction.StopRecording());
                return ctx with { State = ActivationState.Idle, PressedAt = null };

            // Re-press during grace period: cancel timer, resume recording in held mode.
            case ActivationState.Stopping when evt == ActivationEvent.KeyDown:
                actions.Add(new ActivationAction.CancelGraceTimer());
                return ctx with { State = ActivationState.Recording, IsToggled = false, PressedAt = now };

            default:
                return ctx;
        }
    }
}
