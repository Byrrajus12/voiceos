using VoiceOS.Core.Browser;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>Window end-state verification from before/after probes. Two 1920x1032 work areas
/// side by side; no Windows calls.</summary>
public class WindowExpectationTests
{
    private static readonly MonitorArea[] Monitors =
    [
        new(0, true, new WindowRect(0, 0, 1920, 1032)),
        new(1, false, new WindowRect(1920, 0, 1920, 1032))
    ];

    private static WindowInfo Win(string process, long hwnd, int left, int top, int width, int height,
        bool min = false, bool max = false, int monitor = 0, bool foreground = false)
        => new(process, $"{process} window", foreground, hwnd, new WindowRect(left, top, width, height), min, max, monitor);

    private static StateSnapshot Snap(params WindowInfo[] windows)
        => new(DateTimeOffset.UtcNow, windows.FirstOrDefault(static w => w.IsForeground), windows, false, [], null,
            Monitors.Length, Monitors);

    private static Classification Run(WindowExpectation window, StateSnapshot initial, StateSnapshot final)
        => ScenarioEvaluator.EvaluateTurn(
            TestHelpers.Turn(OutcomeClass.Complete, CommandRoute.DirectCapability, ExecutionScopeKind.DirectCapability,
                initial: initial, final: final),
            new Expectation(Outcome: ExpectedOutcome.Complete, Final: new FinalExpectation(Windows: [window]))).Classification;

    private static readonly WindowInfo ChromeFloating = Win("chrome", 1, 200, 100, 1200, 800, foreground: true);

    [Fact]
    public void SnappedLeftGeometryPasses()
        => Assert.Equal(Classification.Pass, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating), Snap(Win("chrome", 1, 0, 0, 960, 1032, foreground: true))));

    [Fact]
    public void SnapToleratesFramePixelDifferences()
        => Assert.Equal(Classification.Pass, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating), Snap(Win("chrome", 1, 7, 0, 953, 1025))));

    [Fact]
    public void SnapAssistTakingForegroundDoesNotInvalidateASuccessfulSnap()
        => Assert.Equal(Classification.Pass, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating),
            Snap(Win("chrome", 1, 0, 0, 960, 1032), Win("explorer", 9, 960, 0, 960, 1032, foreground: true))));

    [Fact]
    public void SnappedRightOnTheSecondMonitorUsesThatMonitorsWorkArea()
        => Assert.Equal(Classification.Pass, Run(new(Process: "chrome", Snapped: SnapSide.Right),
            Snap(Win("chrome", 1, 2000, 100, 1200, 800, monitor: 1)),
            Snap(Win("chrome", 1, 2880, 0, 960, 1032, monitor: 1))));

    [Fact]
    public void MovedButUnsnappedGeometryIsWrongTarget()
        => Assert.Equal(Classification.WrongTarget, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating), Snap(Win("chrome", 1, 100, 100, 800, 600))));

    [Fact]
    public void WrongSideIsWrongTarget()
        => Assert.Equal(Classification.WrongTarget, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating), Snap(Win("chrome", 1, 960, 0, 960, 1032))));

    [Fact]
    public void ClaimedSnapWithNoWindowChangeIsFalseSuccess()
        => Assert.Equal(Classification.FalseSuccess, Run(new(Process: "chrome", Snapped: SnapSide.Left),
            Snap(ChromeFloating), Snap(ChromeFloating)));

    [Theory]
    [InlineData(true, Classification.Pass)]
    [InlineData(false, Classification.WrongTarget)]
    public void MaximizedIsReadFromWindowState(bool maximized, Classification expected)
        => Assert.Equal(expected, Run(new(Process: "mspaint", Maximized: true),
            Snap(Win("mspaint", 2, 100, 100, 800, 600)),
            Snap(Win("mspaint", 2, maximized ? 0 : 150, 0, maximized ? 1920 : 800, maximized ? 1032 : 600, max: maximized))));

    [Fact]
    public void AnUnrelatedAlreadyMaximizedWindowDoesNotSatisfyTheCheck()
        => Assert.Equal(Classification.WrongTarget, Run(new(Process: "mspaint", Maximized: true),
            Snap(Win("mspaint", 2, 0, 0, 1920, 1032, max: true), Win("mspaint", 3, 100, 100, 800, 600)),
            Snap(Win("mspaint", 2, 0, 0, 1920, 1032, max: true), Win("mspaint", 3, 300, 100, 800, 600))));

    [Theory]
    [InlineData(true, Classification.Pass)]
    [InlineData(false, Classification.FalseSuccess)]
    public void MinimizedTargetsTheWindowThatWasForeground(bool minimized, Classification expected)
    {
        var before = Win("mspaint", 2, 100, 100, 800, 600, foreground: true);
        var after = minimized ? Win("mspaint", 2, -32000, -32000, 160, 28, min: true) : before;
        Assert.Equal(expected, Run(new(InitialForeground: true, Minimized: true), Snap(before), Snap(after)));
    }

    [Theory]
    [InlineData(1, Classification.Pass)]
    [InlineData(0, Classification.WrongTarget)]
    public void MonitorChangeComparesTheSameWindowAcrossProbes(int finalMonitor, Classification expected)
        => Assert.Equal(expected, Run(new(InitialForeground: true, MonitorChanged: true),
            Snap(Win("mspaint", 2, 100, 100, 800, 600, monitor: 0, foreground: true)),
            Snap(Win("mspaint", 2, finalMonitor == 1 ? 2020 : 300, 100, 800, 600, monitor: finalMonitor))));

    [Theory]
    [InlineData(false, Classification.Pass)]
    [InlineData(true, Classification.FalseSuccess)]
    public void AbsenceRequiresNoMatchingWindowAfterward(bool stillOpen, Classification expected)
    {
        var charmap = Win("charmap", 4, 50, 50, 500, 400);
        Assert.Equal(expected, Run(new(Process: "charmap", Exists: false),
            Snap(charmap, ChromeFloating), stillOpen ? Snap(charmap, ChromeFloating) : Snap(ChromeFloating)));
    }

    [Theory]
    [InlineData(true, Classification.Pass)]
    [InlineData(false, Classification.FalseSuccess)]
    public void NewWindowMustNotHaveExistedBefore(bool opened, Classification expected)
    {
        var after = opened ? Snap(ChromeFloating, Win("chrome", 5, 300, 200, 1200, 800)) : Snap(ChromeFloating);
        Assert.Equal(expected, Run(new(Process: "chrome", IsNew: true), Snap(ChromeFloating), after));
    }

    [Theory]
    [InlineData("""{ "snapped": "Left" }""")]
    [InlineData("""{ "process": "charmap", "exists": false, "minimized": true }""")]
    [InlineData("""{ "process": "chrome", "snapped": "Up" }""")]
    public void InvalidWindowExpectationsAreRejected(string window)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voiceos-eval-window-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "expect": { "final": { "windows": [ {{window}} ] } } } ]
            """);
        try { Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path)); }
        finally { File.Delete(path); }
    }
}
