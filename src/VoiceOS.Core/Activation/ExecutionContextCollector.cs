using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Activation;

public sealed record CollectedExecutionContext(ExecutionContextSnapshot Snapshot, DecisionState DecisionState,
    RecentTaskFrame? StoredRecentTask);

public static class ExecutionContextCollector
{
    public static async ValueTask<CollectedExecutionContext> CollectAsync(string transcript, IAppCatalog? catalog,
        IDisplayTopologyService? topologyService, IChromeCompanionTransport? transport,
        RecentTaskFrame? stored, LatencyTrace trace, ILogger logger, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var windows = CandidateBuilder.GetOpenWindowsWithTimings(out var timings);
        var apps = catalog is null ? [] : CandidateBuilder.GetInstalledApps(catalog);
        var topology = topologyService?.CaptureTopology() ?? DisplayTopology.Empty;
        var prepMs = timer.Elapsed.TotalMilliseconds;
        var connected = transport?.IsConnected == true;
        IReadOnlyList<BrowserTabInfo> tabs = [];
        if (connected)
        {
            try { tabs = await transport!.ListTabsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Execution context tab inventory failed"); connected = false; }
        }
        var now = DateTimeOffset.UtcNow;
        var validation = RecentTaskPolicy.Validate(stored, connected, tabs, now);
        var foreground = windows.FirstOrDefault(w => w.IsForeground);
        var snapshot = new ExecutionContextSnapshot(foreground, windows, connected, tabs, validation.Exposed, apps);
        snapshot = snapshot with { FrontDoor = FrontDoorContextBuilder.Build(transcript, snapshot, validation.Exposed, now) };
        var state = new DecisionState(transcript, foreground?.Title ?? "", apps, windows,
            [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
            [SnapDirection.Left, SnapDirection.Right], topology);
        trace.Record("context", timer.Elapsed.TotalMilliseconds);
        trace.Record("decision_prep", prepMs);
        logger.LogInformation("Decision prep enum={EnumMs} proc={ProcMs} aumid={AumidMs} hit={Hits} miss={Misses}",
            timings.EnumerateMs, timings.ProcessMs, timings.AumidMs, timings.CacheHits, timings.CacheMisses);
        logger.LogInformation("Recent task stored={Stored} exposed={Exposed} reason={Reason}",
            validation.Stored is not null, validation.Exposed is not null, validation.Reason);
        return new(snapshot, state, validation.Stored);
    }
}
