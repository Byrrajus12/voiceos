using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserPlaybackTests
{
    // -- Open, then "play it" -------------------------------------------------------------------------------------

    private sealed class Compiler : IBrowserStepCompiler
    {
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
            => ValueTask.FromResult<CompiledBrowserTask?>(new(new InteractionPlan(utterance, utterance,
                [new(PlanStepKind.Open, "Open the matching video", Target: "Hello - Adele"), new(PlanStepKind.Act, "Play the video")]),
                new BrowserGoalNormalization(utterance, null, null, null, null, [], utterance, [], SemanticEndState.ContentActive)));
    }

    /// <summary>Answers every question by picking the offered control whose label is the player's own, else the first control.</summary>
    private sealed class Gateway : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            var labels = q["click_target"].Criteria!;
            var pick = labels.FirstOrDefault(l => l.Value.Contains("Play", StringComparison.Ordinal) && !l.Value.Contains("Adele")).Key
                       ?? labels.First().Key;
            var dist = labels.ToDictionary(l => l.Key, l => l.Key == pick ? .95 : .05 / Math.Max(1, labels.Count - 1));
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "CLICK", new Dictionary<string, double> { ["CLICK"] = .95, ["BLOCKED"] = .05 }, .95),
                ["click_target"] = new("choice", pick, dist, .95),
                ["bind"] = new("choice", pick, q["bind"].Criteria!.ToDictionary(l => l.Key, l => l.Key == pick ? .95 : .05 / Math.Max(1, q["bind"].Criteria!.Count - 1)), .95)
            });
        }
    }

    private sealed class Page(string playerButton, string[]? nowPlaying = null, bool navigates = true) : IChromeCompanionTransport
    {
        private bool _opened;
        private bool _playing;
        private int _n;
        public List<string> Acts { get; } = [];
        public bool IsConnected => true;
        private BrowserSnapshot Snap(string s, int t) => new(t, s, $"r{++_n}", _opened && navigates ? "https://site.example/watch?v=a" : "https://site.example/results", "T", "text", false,
            new(1280, 800, 0, 0, 900), _opened && nowPlaying is not null
                ? nowPlaying.Select((n, i) => new BrowserElement($"s{i}", "link", n, true, false, null, "https://site.example/watch?v=a", new(0, 0, 10, 10, true), "")).ToArray()
                : _opened
                ? [new("e2", "button", _playing ? "Pause (k)" : playerButton, true, false, null, null, new(0, 0, 10, 10, true), "")]
                : [new("e1", "link", "Hello - Adele", true, false, null, "https://site.example/watch?v=a", new(0, 0, 10, 10, true), "")]);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default)
        {
            Acts.Add($"{a.Action}:{a.ElementRef}");
            if (a.ElementRef == "e1") { _opened = true; _playing = playerButton == "Pause (k)"; }   // opening the media may start it
            else if (a.ElementRef == "e2") _playing = true;
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private sealed class CountingRecovery : IBrowserBlockerAssessor, IBrowserStepRepair
    {
        public int Calls { get; private set; }
        public ValueTask<BlockerAssessment?> AssessAsync(BlockerRequest r, CancellationToken c = default) { Calls++; return ValueTask.FromResult<BlockerAssessment?>(null); }
        public ValueTask<StepRepairResult?> RepairAsync(StepRepairRequest r, CancellationToken c = default) { Calls++; return ValueTask.FromResult<StepRepairResult?>(null); }
    }

    private static readonly BrowserExecutionScope Active = new(BrowserScopeKind.ActiveTab, 1, "https://site.example/results", ExplicitSelection: true);

    [Fact]
    public async Task PlayAfterAnOpenThatAlreadyStartedPlayback_CompletesWithZeroActions_AndNoRecovery()
    {
        var page = new Page("Pause (k)");                 // the opened resource is already playing (its control offers to pause)
        var recovery = new CountingRecovery();
        var service = new BrowserInteractionService(page, new Gateway(), new Compiler(), blocker: recovery, repair: recovery);

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["CLICK:e1"], page.Acts);            // only the Open; nothing is clicked again
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task PlayAfterAnOpenThatDidNotStartPlayback_StillPlays()
    {
        var page = new Page("Play (k)");                  // opened but paused
        var service = new BrowserInteractionService(page, new Gateway(), new Compiler());

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Equal(["CLICK:e1", "CLICK:e2"], page.Acts);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public async Task PlayAfterAnOpenWhoseSurfaceShowsNowPlaying_CompletesWithZeroActions_AndNeverAsksWhichOne()
    {
        var page = new Page("Play (k)", ["6:07 Now playing", "4:50 Now playing"]);   // the page the activation led to states playback
        var recovery = new CountingRecovery();
        var service = new BrowserInteractionService(page, new Gateway(), new Compiler(), blocker: recovery, repair: recovery);

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Null(result.Pending);
        Assert.Equal(["CLICK:e1"], page.Acts);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task DurationAndNowPlayingControls_AreNeverOfferedAsAChoiceForABarePlay_EvenWhenPlaybackIsNotEstablished()
    {
        // The open did not lead anywhere (the URL is unchanged), so "Now playing" is not evidence here: the Play step runs, but as an
        // operation, not as a question about which duration badge the user meant.
        var page = new Page("Play (k)", ["6:07 Now playing", "4:50 Now playing"], navigates: false);
        var service = new BrowserInteractionService(page, new Gateway(), new Compiler());

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Null(result.Pending);
        Assert.Null(service.PendingChoice);
    }
}
