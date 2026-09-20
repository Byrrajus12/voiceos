using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;

namespace VoiceOS.Eval.Evaluation;

public static class JevEvaluationHarness
{
    private record TestCase(
        string Utterance,
        VoiceAction ExpectedAction,
        string? ExpectedTarget = null);

    private static readonly TestCase[] Cases =
    [
        // Clear open-app commands
        new("open chrome", VoiceAction.OpenApp, "Google Chrome"),
        new("launch chrome", VoiceAction.OpenApp, "Google Chrome"),
        new("pull up chrome", VoiceAction.OpenApp, "Google Chrome"),
        new("go to spotify", VoiceAction.OpenApp, "Spotify"),
        new("open firefox", VoiceAction.OpenApp, "Firefox"),
        new("launch visual studio code", VoiceAction.OpenApp, "Visual Studio Code"),
        new("open edge", VoiceAction.OpenApp, "Microsoft Edge"),
        new("open terminal", VoiceAction.OpenApp, "Windows Terminal"),
        new("open discord", VoiceAction.OpenApp, "Discord"),
        new("open slack", VoiceAction.OpenApp, "Slack"),

        // Window focus
        new("switch to vscode", VoiceAction.FocusWindow, null),
        new("switch to chrome", VoiceAction.FocusWindow, null),
        new("go back to notepad", VoiceAction.FocusWindow, null),

        // Window management
        new("close this", VoiceAction.CloseCurrentWindow),
        new("close this window", VoiceAction.CloseCurrentWindow),
        new("maximize this", VoiceAction.MaximizeCurrentWindow),
        new("minimize this", VoiceAction.MinimizeCurrentWindow),
        new("snap this right", VoiceAction.SnapCurrentWindow),
        new("snap this left", VoiceAction.SnapCurrentWindow),
        new("snap to the right", VoiceAction.SnapCurrentWindow),

        // Media
        new("pause this", VoiceAction.MediaControl),
        new("play", VoiceAction.MediaControl),
        new("next song", VoiceAction.MediaControl),
        new("previous track", VoiceAction.MediaControl),
        new("skip this song", VoiceAction.MediaControl),

        // Volume
        new("volume thirty", VoiceAction.SetVolume),
        new("set volume to fifty percent", VoiceAction.SetVolume),
        new("turn it up", VoiceAction.AdjustVolume),
        new("volume down", VoiceAction.AdjustVolume),

        // Non-commands (should return None)
        new("the weather is nice today", VoiceAction.None),
        new("I need to think about this", VoiceAction.None),
        new("hmm let me see", VoiceAction.None),
        new("okay so", VoiceAction.None),

        // Ambiguous (may return RequiresClarification or None/Rejected)
        new("the other one", VoiceAction.None),
        new("go back", VoiceAction.None),
        new("that thing", VoiceAction.None),

        // Unknown app (should return OpenApp with low confidence or RequiresClarification)
        new("open flurblegrax", VoiceAction.OpenApp),
    ];

    public static async Task RunAsync(ILoggerFactory loggerFactory)
    {
        var apiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("ERROR: TYPESAFE_API_KEY environment variable not set.");
            return;
        }

        var logger = loggerFactory.CreateLogger<TypeSafeJevDecisionEngine>();
        var engine = new TypeSafeJevDecisionEngine(
            apiKey, new HttpClient(), "jev-latest", 0.35, 0.40, logger);

        var state = BuildBaseState();

        Console.WriteLine($"{"Utterance",-40} {"Expected",-25} {"Actual",-25} {"Conf",6} {"Match",6} {"Ms",6}");
        Console.WriteLine(new string('-', 115));

        int total = 0, matched = 0;

        foreach (var tc in Cases)
        {
            var sw = Stopwatch.StartNew();
            DecisionResult result;
            try
            {
                var decState = state with { Transcript = tc.Utterance };
                result = await engine.DecideAsync(decState);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{tc.Utterance,-40} {"ERROR",-52} {ex.Message}");
                total++;
                continue;
            }
            sw.Stop();

            bool match = result.Plan.Action == tc.ExpectedAction;
            if (match) matched++;
            total++;

            Console.WriteLine(
                $"{Truncate(tc.Utterance, 38),-40} {tc.ExpectedAction,-25} {result.Plan.Action,-25} {result.Plan.Confidence,6:F2} {(match ? "YES" : "NO"),6} {sw.ElapsedMilliseconds,5}ms");

            if (!match)
            {
                Console.WriteLine($"  ^ Rejection: {result.Plan.RejectionReason}");
                foreach (var (k, v) in result.RawAnswers)
                    Console.WriteLine($"    {k}: choice={v.SelectedChoice} conf={v.Confidence:F2}");
            }

            await Task.Delay(50); // avoid hammering the API
        }

        Console.WriteLine(new string('-', 115));
        Console.WriteLine($"Result: {matched}/{total} matched ({100.0 * matched / total:F0}%)");
    }

    private static DecisionState BuildBaseState()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        return new DecisionState(
            Transcript: "",
            ForegroundApp: "Notepad",
            InstalledApps: apps,
            OpenWindows: [
                new WindowCandidate("w0", "notepad", "Untitled - Notepad", true),
                new WindowCandidate("w1", "chrome", "Google - Chrome"),
                new WindowCandidate("w2", "Code", "Program.cs - VoiceOS"),
            ],
            AvailableMediaOps: [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
            AvailableSnapDirs: [SnapDirection.Left, SnapDirection.Right]);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
