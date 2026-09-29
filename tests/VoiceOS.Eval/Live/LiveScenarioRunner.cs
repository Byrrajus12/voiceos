using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Config;
using VoiceOS.Core.Execution;

namespace VoiceOS.Eval.Live;

/// <summary>Drives selected scenarios against the live product graph. Real Windows/Chrome/mutex
/// calls — validated physically, not unit tested.</summary>
public sealed class LiveScenarioRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        WriteIndented = false
    };

    public async Task<int> RunAsync(LiveEvalOptions options, IReadOnlyList<Scenario> selected,
        IReadOnlyList<SkippedScenario> skipped)
    {
        using var mutex = new Mutex(true, VoiceOS.VoiceOSProduct.SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Console.WriteLine("VoiceOS tray app is running; exit it before a live eval (both need the Chrome Companion pipe).");
            return 2;
        }

        DotEnvLoader.Load();

        var runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var outRoot = options.OutDir ?? ResolveDefaultOutDir();
        var runDir = Path.Combine(outRoot, runId);
        Directory.CreateDirectory(runDir);
        var resultsPath = Path.Combine(runDir, "results.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");
        var productLogPath = Path.Combine(runDir, "product.log");

        using var fileLogProvider = new FileLoggerProvider(productLogPath);
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            // The file keeps full Information product logs; the console stays quiet unless --verbose.
            b.SetMinimumLevel(LogLevel.Information);
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null,
                options.Verbose ? LogLevel.Information : LogLevel.Warning);
            b.AddProvider(fileLogProvider);
        });

        // Match the tray app's process-wide DPI mode so product window operations and the probe
        // see the same physical-pixel coordinates as a real voice activation would.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        var product = VoiceOS.VoiceOSProduct.Build(loggerFactory, includeSpeech: false);
        File.WriteAllText(Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(new {
            RunId = runId, StartedAt = DateTimeOffset.UtcNow,
            product.Config.FrontDoorGrounding, product.Config.SpeculativeDirectDecision, product.Config.DirectRescue,
            options.Repeat, ScenarioIds = selected.Select(s => s.Id).ToArray(),
            AssemblyVersion = typeof(VoiceOS.VoiceOSProduct).Assembly.ManifestModule.ModuleVersionId
        }, JsonOptions));
        try
        {
            var results = new List<ScenarioResult>();
            using var resultsWriter = new StreamWriter(File.Open(resultsPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };

            foreach (var skip in skipped)
            {
                var result = SkippedResult(runId, skip.Scenario, skip.Reason);
                results.Add(result);
                resultsWriter.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }

            var aborted = false;
            foreach (var scenario in selected)
            {
                for (var attempt = 1; attempt <= options.Repeat; attempt++)
                {
                    if (aborted)
                    {
                        var skippedResult = SkippedResult(runId, scenario, "run aborted after a contaminating timeout",
                            attempt);
                        results.Add(skippedResult);
                        resultsWriter.WriteLine(JsonSerializer.Serialize(skippedResult, JsonOptions));
                        continue;
                    }
                    var (result, contaminated) = await RunScenarioAsync(product, runId, scenario, attempt).ConfigureAwait(false);
                    results.Add(result);
                    resultsWriter.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    PrintScenarioLine(result);
                    if (contaminated) aborted = true;
                }
            }

            var summary = EvalAggregator.Summarize(results);
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            PrintSummary(summary, runDir);
            return 0;
        }
        finally
        {
            product.Dispose();
        }
    }

    private async Task<(ScenarioResult Result, bool Contaminated)> RunScenarioAsync(
        VoiceOS.VoiceOSProduct product, string runId, Scenario scenario, int attempt)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var wall = Stopwatch.StartNew();
        var probe = new MachineStateProbe(product);
        var preconditionChecks = new List<CheckResult>();
        var setupOutcomes = new List<StepOutcome>();
        var cleanupOutcomes = new List<StepOutcome>();
        var turns = new List<TurnRecord>();
        // Snapshots around setup identify what setup created, so it can be undone afterwards.
        var startsProcesses = scenario.Setup.Any(static s => s.Kind == SetupStepKind.StartProcess);
        StateSnapshot? beforeSetup = startsProcesses ? await probe.CaptureAsync().ConfigureAwait(false) : null;
        StateSnapshot? afterSetup = null;

        try
        {
            foreach (var step in scenario.Setup)
            {
                var outcome = await RunSetupStepAsync(product, step, isCleanup: false).ConfigureAwait(false);
                setupOutcomes.Add(outcome);
                if (!outcome.Ok)
                    return (Finish(runId, scenario, attempt, startedAt, wall.Elapsed.TotalMilliseconds,
                        Classification.SetupFailure, preconditionChecks, setupOutcomes, cleanupOutcomes, turns,
                        $"setup step '{step.Kind}' failed: {outcome.Detail}"), false);
            }
            if (beforeSetup is not null) afterSetup = await probe.CaptureAsync().ConfigureAwait(false);

            foreach (var precondition in scenario.Preconditions)
            {
                var check = await CheckPreconditionAsync(product, probe, precondition).ConfigureAwait(false);
                preconditionChecks.Add(check);
            }
            if (preconditionChecks.Any(c => !c.Passed))
                return (Finish(runId, scenario, attempt, startedAt, wall.Elapsed.TotalMilliseconds,
                    Classification.EnvironmentMismatch, preconditionChecks, setupOutcomes, cleanupOutcomes, turns,
                    "one or more preconditions were not met"), false);

            // Probe after preconditions: a companionConnected wait can change what is observable.
            var initialProbe = await probe.CaptureAsync().ConfigureAwait(false);

            var contaminated = false;
            var priorFinal = initialProbe;
            for (var i = 0; i < scenario.Turns.Count; i++)
            {
                var turnSpec = scenario.Turns[i];
                var turnInitial = i == 0 ? initialProbe : priorFinal;
                var (activationRun, timedOut, stuck) = await RunTranscriptWithTimeoutAsync(
                    product, turnSpec.Transcript, scenario.TimeoutSeconds).ConfigureAwait(false);
                if (stuck)
                {
                    contaminated = true;
                    break;
                }
                if (turnSpec.SettleMs > 0)
                    await Task.Delay(turnSpec.SettleMs).ConfigureAwait(false);
                var turnFinal = await probe.CaptureAsync().ConfigureAwait(false);
                priorFinal = turnFinal;

                TurnRecord turn;
                if (activationRun is null)
                {
                    turn = TimedOutTurn(i, turnSpec.Transcript, turnInitial, turnFinal);
                }
                else
                {
                    // Eval observes unused work for accounting, after the product has completed.
                    // CompletedPostSttMs preserves product latency; this does not mark the result used.
                    if (activationRun.SpeculativeTask is { } speculative) await speculative.ConfigureAwait(false);
                    turn = RunMapper.Map(i, turnSpec.Transcript, activationRun, turnInitial, turnFinal);
                    if (timedOut) turn = turn with { OutcomeClass = OutcomeClass.Timeout };
                }
                turn = ScenarioEvaluator.EvaluateTurn(turn, turnSpec.Expect);
                turns.Add(turn);
                if (!ScenarioResult.SuccessSet.Contains(turn.Classification))
                    break;
            }

            foreach (var step in scenario.Cleanup)
                cleanupOutcomes.Add(await RunSetupStepAsync(product, step, isCleanup: true,
                    initialProbe.Tabs.Select(static t => t.TabId).ToHashSet()).ConfigureAwait(false));

            var classification = turns.Count == 0 ? Classification.HarnessError : ScenarioEvaluator.EvaluateScenario(turns);
            return (Finish(runId, scenario, attempt, startedAt, wall.Elapsed.TotalMilliseconds, classification,
                preconditionChecks, setupOutcomes, cleanupOutcomes, turns, null), contaminated);
        }
        catch (Exception ex)
        {
            return (Finish(runId, scenario, attempt, startedAt, wall.Elapsed.TotalMilliseconds,
                Classification.HarnessError, preconditionChecks, setupOutcomes, cleanupOutcomes, turns, ex.Message), false);
        }
        finally
        {
            // Runs after every exit (including precondition mismatches) so setup state never
            // contaminates later scenarios. The result's cleanup list is the same instance.
            if (beforeSetup is not null)
            {
                try
                {
                    var current = await probe.CaptureAsync().ConfigureAwait(false);
                    cleanupOutcomes.AddRange(await UndoSetupAsync(product, SetupCleanup.Plan(scenario.Setup,
                        beforeSetup, afterSetup ?? current, current)).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    cleanupOutcomes.Add(new("undoSetup", true, $"best-effort cleanup failed: {ex.Message}"));
                }
            }
        }
    }

    private const string SetupCleanupSession = "voiceos-eval-setup-cleanup";

    /// <summary>Closes setup-created app windows (WM_CLOSE) and setup-created web tabs. The companion
    /// never closes user tabs, so a tab is closed with Ctrl+W only after the companion focused it and
    /// both the companion and the OS confirm it is the active tab of the foreground Chrome window.</summary>
    private static async Task<IReadOnlyList<StepOutcome>> UndoSetupAsync(VoiceOS.VoiceOSProduct product,
        SetupCleanupPlan plan)
    {
        var outcomes = new List<StepOutcome>();
        foreach (var reason in plan.Skipped)
            outcomes.Add(new("undoSetup", true, $"left open: {reason}"));

        if (plan.Windows.Count > 0)
        {
            var windows = CandidateBuilder.GetOpenWindows();
            var service = new WindowService(NullLogger<WindowService>.Instance);
            foreach (var hwnd in plan.Windows)
            {
                var target = windows.FirstOrDefault(w => w.Hwnd == (nint)hwnd);
                if (target is null) continue;
                var closed = service.Close(target.Id, windows);
                outcomes.Add(new("undoSetup.window", true,
                    $"{target.ProcessName} '{target.Title}': {closed.Status}"));
            }
        }

        foreach (var tab in plan.Tabs)
        {
            if (!product.ChromeCompanion.IsConnected)
            {
                outcomes.Add(new("undoSetup.tab", true, $"tab {tab.TabId} left open: companion not connected"));
                continue;
            }
            outcomes.Add(new("undoSetup.tab", true, await CloseSetupTabAsync(product, tab).ConfigureAwait(false)));
        }
        return outcomes;
    }

    private static async Task<string> CloseSetupTabAsync(VoiceOS.VoiceOSProduct product, SetupTabToClose planned)
    {
        async Task<VoiceOS.Core.Browser.BrowserTabInfo?> Find()
            => (await product.ChromeCompanion.ListTabsAsync().ConfigureAwait(false))
                .FirstOrDefault(t => t.TabId == planned.TabId);
        var tab = await Find().ConfigureAwait(false);
        if (tab?.Url is null) return $"tab {planned.TabId} already closed";
        try
        {
            // A tab the product adopted during the scenario must be selected under its own session.
            await product.ChromeCompanion.SelectTabAsync(tab.SessionId ?? SetupCleanupSession, tab.TabId, tab.Url,
                requireActive: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"tab {tab.TabId} left open: not selectable ({ex.Message})";
        }
        await Task.Delay(300).ConfigureAwait(false);
        var selected = await Find().ConfigureAwait(false);
        var foreground = MachineStateProbe.RawForeground();
        if (selected is null) return $"tab {planned.TabId} already closed";
        if (!selected.Active || !string.Equals(foreground?.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
            return $"tab {tab.TabId} left open: could not confirm it is the focused Chrome tab";
        SendCtrlW();
        await Task.Delay(400).ConfigureAwait(false);
        return await Find().ConfigureAwait(false) is null
            ? $"closed setup tab {tab.TabId} ({tab.Url})"
            : $"tab {tab.TabId} left open: close was not observed";
    }

    private static void SendCtrlW()
    {
        const byte control = 0x11, w = 0x57;
        const uint keyUp = 0x0002;
        keybd_event(control, 0, 0, 0);
        keybd_event(w, 0, 0, 0);
        keybd_event(w, 0, keyUp, 0);
        keybd_event(control, 0, keyUp, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, nuint extraInfo);

    private static TurnRecord TimedOutTurn(int index, string transcript, StateSnapshot initial, StateSnapshot final)
        => new(index, transcript, "", DateTimeOffset.UtcNow, null, "Unrouted", "Timeout", OutcomeClass.Timeout,
            null, null, null, null, null, false, null,
            new ExecutionInfo(null, null, null, null, null, [], [], null, []),
            new LatencyInfo(0, null, [], new Dictionary<string, double>(), new Dictionary<string, int>()),
            new CountInfo(0, 0, 0, 0, 0, 0), initial, final, [], Classification.Timeout);

    private static async Task<(ActivationRun? Run, bool TimedOut, bool Stuck)> RunTranscriptWithTimeoutAsync(
        VoiceOS.VoiceOSProduct product, string transcript, int timeoutSeconds)
    {
        var task = product.Orchestrator.RunTranscriptAsync(transcript);
        var primary = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);
        if (primary == task) return (await task.ConfigureAwait(false), false, false);

        var grace = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (grace == task) return (await task.ConfigureAwait(false), true, false);
        return (null, true, true);
    }

    private static ScenarioResult Finish(string runId, Scenario scenario, int attempt, DateTimeOffset startedAt,
        double wallMs, Classification classification, IReadOnlyList<CheckResult> preconditions,
        IReadOnlyList<StepOutcome> setup, IReadOnlyList<StepOutcome> cleanup, IReadOnlyList<TurnRecord> turns,
        string? note)
        => new(runId, scenario.Id, scenario.Name, scenario.Family, scenario.Tags, attempt, startedAt, wallMs,
            classification, ScenarioResult.SuccessSet.Contains(classification), preconditions, setup, cleanup,
            turns, note);

    private static ScenarioResult SkippedResult(string runId, Scenario scenario, string reason, int attempt = 1)
        => new(runId, scenario.Id, scenario.Name, scenario.Family, scenario.Tags, attempt, DateTimeOffset.UtcNow, 0,
            Classification.Skipped, false, [], [], [], [], reason);

    private static async Task<StepOutcome> RunSetupStepAsync(VoiceOS.VoiceOSProduct product, SetupStep step, bool isCleanup,
        IReadOnlySet<int>? tabsBeforeScenario = null)
    {
        try
        {
            switch (step.Kind)
            {
                case SetupStepKind.StartProcess:
                    Process.Start(new ProcessStartInfo(step.File!) { Arguments = step.Args ?? "", UseShellExecute = true });
                    return new(step.Kind.ToString(), true, step.File);
                case SetupStepKind.FocusWindow:
                    var windows = CandidateBuilder.GetOpenWindows();
                    var target = windows.FirstOrDefault(w =>
                        string.Equals(w.ProcessName, step.Process, StringComparison.OrdinalIgnoreCase));
                    if (target is null) return new(step.Kind.ToString(), isCleanup, $"no open window for process '{step.Process}'");
                    var focusResult = new WindowService(NullLogger<WindowService>.Instance).Focus(target.Id, windows);
                    return new(step.Kind.ToString(), focusResult.Status == ExecutionStatus.Success, focusResult.Detail);
                case SetupStepKind.Wait:
                    await Task.Delay(step.Ms).ConfigureAwait(false);
                    return new(step.Kind.ToString(), true, $"{step.Ms}ms");
                case SetupStepKind.CloseNewTabs:
                    if (!isCleanup) return new(step.Kind.ToString(), true, "closeNewTabs is cleanup-only; no-op");
                    if (!product.ChromeCompanion.IsConnected) return new(step.Kind.ToString(), true, "companion not connected");
                    var tabs = await product.ChromeCompanion.ListTabsAsync().ConfigureAwait(false);
                    // Only tabs this scenario created; pre-existing VoiceOS task tabs are left alone.
                    var owned = tabs.Where(t => t.Provenance == VoiceOS.Core.Browser.BrowserTabProvenance.VoiceOs
                        && t.SessionId is not null && tabsBeforeScenario?.Contains(t.TabId) == false).ToArray();
                    foreach (var tab in owned)
                    {
                        try { await product.ChromeCompanion.CloseTaskTabAsync(tab.SessionId!, tab.TabId).ConfigureAwait(false); }
                        catch (Exception) { /* best-effort */ }
                    }
                    return new(step.Kind.ToString(), true, $"closed {owned.Length} tab(s)");
                default:
                    return new(step.Kind.ToString(), false, "unhandled setup step kind");
            }
        }
        catch (Exception ex)
        {
            return new(step.Kind.ToString(), isCleanup, ex.Message);
        }
    }

    private static async Task<CheckResult> CheckPreconditionAsync(VoiceOS.VoiceOSProduct product,
        MachineStateProbe probe, Precondition p)
    {
        switch (p.Kind)
        {
            case PreconditionKind.CompanionConnected:
                var deadline = DateTime.UtcNow.AddSeconds(p.WaitSeconds);
                while (!product.ChromeCompanion.IsConnected && DateTime.UtcNow < deadline)
                    await Task.Delay(500).ConfigureAwait(false);
                return new("companionConnected", CheckCategory.Precondition, product.ChromeCompanion.IsConnected,
                    "connected", product.ChromeCompanion.IsConnected ? "connected" : "not connected");
            case PreconditionKind.AppInstalled:
                var installed = product.Catalog.GetAll().Any(a =>
                    string.Equals(a.DisplayName, p.App, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Id, p.App, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.ProcessName, p.App, StringComparison.OrdinalIgnoreCase));
                return new("appInstalled", CheckCategory.Precondition, installed, p.App, installed ? "found" : "not found");
            case PreconditionKind.ForegroundProcess:
                var fgSnapshot = await probe.CaptureAsync().ConfigureAwait(false);
                var fgMatches = string.Equals(fgSnapshot.Foreground?.ProcessName, p.Process, StringComparison.OrdinalIgnoreCase);
                return new("foregroundProcess", CheckCategory.Precondition, fgMatches, p.Process, fgSnapshot.Foreground?.ProcessName);
            case PreconditionKind.WindowOpen:
                var winSnapshot = await probe.CaptureAsync().ConfigureAwait(false);
                var winOpen = winSnapshot.Windows.Any(w => string.Equals(w.ProcessName, p.Process, StringComparison.OrdinalIgnoreCase));
                return new("windowOpen", CheckCategory.Precondition, winOpen, p.Process, winOpen.ToString());
            case PreconditionKind.BrowserTabs:
                var tabSnapshot = await probe.CaptureAsync().ConfigureAwait(false);
                var enough = tabSnapshot.Tabs.Count >= p.Min;
                return new("browserTabs", CheckCategory.Precondition, enough, $">= {p.Min}", tabSnapshot.Tabs.Count.ToString());
            case PreconditionKind.ActiveTabOrigin:
                var activeSnapshot = await probe.CaptureAsync().ConfigureAwait(false);
                var origin = activeSnapshot.ActiveTab?.Origin;
                var contains = origin is not null && p.Contains is not null
                    && origin.Contains(p.Contains, StringComparison.OrdinalIgnoreCase);
                return new("activeTabOrigin", CheckCategory.Precondition, contains, p.Contains, origin);
            case PreconditionKind.MinMonitors:
                var monSnapshot = await probe.CaptureAsync().ConfigureAwait(false);
                var enoughMonitors = monSnapshot.MonitorCount >= p.Count;
                return new("minMonitors", CheckCategory.Precondition, enoughMonitors, $">= {p.Count}", monSnapshot.MonitorCount.ToString());
            default:
                return new(p.Kind.ToString(), CheckCategory.Precondition, false, null, "unhandled precondition kind");
        }
    }

    private static void PrintScenarioLine(ScenarioResult result)
    {
        var turn = result.Turns.LastOrDefault();
        Console.WriteLine($"[{result.Classification}] {result.ScenarioId}  outcome={turn?.ProductOutcome ?? "-"} " +
            $"route={turn?.Route?.Route.ToString() ?? "-"} scope={turn?.Scope?.Kind.ToString() ?? "-"} " +
            $"post_stt_ms={turn?.Latency.TotalPostSttMs.ToString("F0") ?? "-"} " +
            $"first_action_ms={turn?.Latency.FirstActionMs?.ToString("F0") ?? "-"} " +
            $"actions={turn?.Counts.Actions ?? 0} decisions={turn?.Counts.BrowserDecisions ?? 0}");
    }

    private static void PrintSummary(EvalSummary summary, string runDir)
    {
        Console.WriteLine();
        Console.WriteLine($"=== VoiceOS Live Eval Summary ===");
        Console.WriteLine($"Attempts: {summary.Attempts}  Successes: {summary.Successes}  Rate: {summary.SuccessRate:P0}");
        foreach (var (family, rate) in summary.PerFamily.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {family}: {rate.Successes}/{rate.Attempts} ({rate.Rate:P0})");
        Console.WriteLine("Classifications:");
        foreach (var (classification, count) in summary.ClassificationCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {classification}: {count}");
        Console.WriteLine($"Latency (post-STT ms) p50={summary.TotalPostSttMs.P50:F0} p90={summary.TotalPostSttMs.P90:F0} p95={summary.TotalPostSttMs.P95:F0}");
        Console.WriteLine($"Latency (first action ms) p50={summary.FirstActionMs.P50:F0} p90={summary.FirstActionMs.P90:F0} p95={summary.FirstActionMs.P95:F0}");
        if (summary.FrontDoor is { } f)
        {
            Console.WriteLine($"PreExecUnnecessaryClarify={f.PreExecUnnecessaryClarify} PostActionUnnecessaryClarify={f.PostActionUnnecessaryClarify}");
            Console.WriteLine($"Rescue fired={f.RescueFired} passed={f.RescuePassed} contextual={f.ContextualDirectChoices} unsafe_browser={f.UnsafeBrowserRescues}");
            Console.WriteLine($"Speculative started={f.SpeculativeStarted} used={f.SpeculativeUsed} unused={f.SpeculativeUnused} waste={f.SpeculativeWastePercent:F1}%");
            foreach (var (lane, metrics) in f.ByLane)
                Console.WriteLine($"{lane}: front_door p50={metrics.FrontDoorMs.P50:F0} p90={metrics.FrontDoorMs.P90:F0} calls={metrics.MeanCalls:F2} hops={metrics.MeanSequentialHops:F2}");
        }
        Console.WriteLine($"Output: {runDir}");
    }

    private static string ResolveDefaultOutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceOS.sln")))
                return Path.Combine(dir.FullName, "artifacts", "eval");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "eval-results");
    }
}
