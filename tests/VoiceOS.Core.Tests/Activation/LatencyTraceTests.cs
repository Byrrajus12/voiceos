using Xunit;
using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Tests.Activation;

public sealed class LatencyTraceTests
{
    [Fact]
    public async Task ConcurrentCallsAndUnusedHeadsRemainVisibleAfterTerminalTiming()
    {
        var trace = LatencyTrace.Begin("accounting");
        using (trace.BeginModelCall("route"))
        {
            await Task.Run(() => {
                using var direct = trace.BeginModelCall("direct_base");
                trace.RecordHead(new("direct", "action_kind", "CLOSE_WINDOW", .9, "NONE", .1, .8, .9));
            });
        }
        Assert.Equal(2, trace.ModelCalls.Count);
        Assert.Single(trace.Heads);
        trace.Record("front_door", 1);
        trace.Replace("front_door", 2);
        Assert.Equal(2, Assert.Single(trace.Stages).ElapsedMs);
    }

    [Fact]
    public async Task AmbientTraceFlowsIntoAwaitedCalleesAndStaysLocalToItsFlow()
    {
        LatencyTrace? observedElsewhere = null;
        await Task.Run(() => observedElsewhere = LatencyTrace.Current);

        await RunActivationAsync();

        Assert.Null(observedElsewhere);
        Assert.Null(LatencyTrace.Current);

        static async Task RunActivationAsync()
        {
            var trace = LatencyTrace.Begin("a1");
            await DeepStageAsync();
            Assert.Equal(["route", "browser_decision"], trace.Stages.Select(static stage => stage.Name));
        }

        static async Task DeepStageAsync()
        {
            await Task.Yield();
            LatencyTrace.Current?.Record("route", 5);
            LatencyTrace.Current?.Record("browser_decision", 7);
        }
    }

    [Fact]
    public void FormatListsRepeatedStagesInOrderAndKeepsTheFirstExternalAction()
    {
        var trace = new LatencyTrace("a2");
        trace.Record("route", 120.4);
        trace.Record("browser_decision", 150);
        trace.MarkExternalAction();
        var first = trace.FirstExternalActionMs;
        trace.Record("browser_decision", 210);
        trace.MarkExternalAction();

        var text = trace.Format();

        Assert.Equal(first, trace.FirstExternalActionMs);
        Assert.StartsWith("route_ms=120 browser_decision_ms=[150,210] first_action_ms=", text);
        Assert.Contains(" total_post_stt_ms=", text);
    }

    [Fact]
    public void StageStartIsPositionedBeforeItsRecordingPoint()
    {
        var trace = new LatencyTrace("a3");
        trace.Record("scope", 0);
        trace.Record("normalization", 1_000_000);

        Assert.All(trace.Stages, stage => Assert.True(stage.StartMs >= 0));
        Assert.Equal(0, trace.Stages[1].StartMs);
    }
}
