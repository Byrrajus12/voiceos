using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public class ScenarioLoaderTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voiceos-eval-scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ShorthandTranscriptNormalizesToSingleTurn()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "hello", "expect": { "outcome": "Complete" } } ]
            """);
        try
        {
            var scenarios = ScenarioLoader.LoadFile(path);
            Assert.Single(scenarios);
            var turns = scenarios[0].Turns;
            Assert.Single(turns);
            Assert.Equal("hello", turns[0].Transcript);
            Assert.Equal(ExpectedOutcome.Complete, turns[0].Expect!.Outcome);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownPreconditionKindThrows()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "preconditions": [ { "kind": "notARealKind" } ] } ]
            """);
        try
        {
            var ex = Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path));
            Assert.Contains("a.b", ex.Message);
            Assert.Contains("notARealKind", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownSetupStepKindThrows()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "setup": [ { "kind": "doTheThing" } ] } ]
            """);
        try
        {
            Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicateIdAcrossFilesThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voiceos-eval-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"),
                """[ { "id": "dup.id", "name": "n", "family": "f", "transcript": "x" } ]""");
            File.WriteAllText(Path.Combine(dir, "b.json"),
                """[ { "id": "dup.id", "name": "n2", "family": "f", "transcript": "y" } ]""");
            Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadDirectory(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DefaultsAreAppliedWhenOmitted()
    {
        var path = WriteTemp("""[ { "id": "a.b", "name": "n", "family": "f", "transcript": "x" } ]""");
        try
        {
            var scenario = ScenarioLoader.LoadFile(path)[0];
            Assert.True(scenario.Safety.Unattended);
            Assert.False(scenario.Safety.MutatesExternalState);
            Assert.Equal(60, scenario.TimeoutSeconds);
            Assert.Equal(800, scenario.SettleMs);
            Assert.Empty(scenario.Tags);
            Assert.Empty(scenario.Preconditions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingTranscriptAndTurnsThrows()
    {
        var path = WriteTemp("""[ { "id": "a.b", "name": "n", "family": "f" } ]""");
        try { Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CompanionConnectedPreconditionDefaultsWaitSecondsTo40()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "preconditions": [ { "kind": "companionConnected" } ] } ]
            """);
        try
        {
            var scenario = ScenarioLoader.LoadFile(path)[0];
            Assert.Equal(40, scenario.Preconditions[0].WaitSeconds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MisspelledExpectationFieldThrowsInsteadOfDroppingTheCheck()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "expect": { "finall": { "foregroundProcess": "chrome" } } } ]
            """);
        try { Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TurnsInheritScenarioSettleUnlessOverridden()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "settleMs": 3000,
                "turns": [ { "transcript": "one" }, { "transcript": "two", "settleMs": 100 } ] } ]
            """);
        try
        {
            var turns = ScenarioLoader.LoadFile(path)[0].Turns;
            Assert.Equal(3000, turns[0].SettleMs);
            Assert.Equal(100, turns[1].SettleMs);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"mediaOperation\": \"Rewind\"")]
    [InlineData("\"mediaOperation\": 42")]
    [InlineData("\"setVolume\": 150")]
    [InlineData("\"adjustVolume\": { \"direction\": \"Sideways\" }")]
    [InlineData("\"adjustVolume\": { \"amount\": 10 }")]
    public void MalformedDirectOperandExpectationsAreRejected(string field)
    {
        var path = WriteTemp($$"""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x", "expect": { {{field}} } } ]
            """);
        try { Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DirectOperandExpectationsParse()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x",
                "expect": { "mediaOperation": "Previous", "setVolume": 35, "adjustVolume": { "direction": "Down", "amount": 10 } } } ]
            """);
        try
        {
            var expect = ScenarioLoader.LoadFile(path)[0].Turns[0].Expect!;
            Assert.Equal(VoiceOS.Core.Decision.MediaOperation.Previous, expect.MediaOperation);
            Assert.Equal(35, expect.SetVolume);
            Assert.Equal(new AdjustVolumeExpectation(VoiceOS.Core.Decision.VolumeDirection.Down, 10), expect.AdjustVolume);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AcceptedOutcomesAndAdvisoryFieldsLoad()
    {
        var path = WriteTemp("""
            [ { "id": "a.b", "name": "n", "family": "f", "transcript": "x", "expect": {
                "outcome": "Complete", "acceptedOutcomes": ["Complete", "Clarify"],
                "scope": ["ActiveTab", "NewTaskTab"], "preferredScope": ["ActiveTab"], "efficiencyAdvisory": true } } ]
            """);
        try
        {
            var expect = ScenarioLoader.LoadFile(path)[0].Turns[0].Expect!;
            Assert.Equal([ExpectedOutcome.Complete, ExpectedOutcome.Clarify], expect.AllAcceptedOutcomes);
            Assert.Equal(ExpectedOutcome.Complete, expect.PrimaryOutcome);
            Assert.Equal([ScopeExpectation.ActiveTab], expect.PreferredScope);
            Assert.True(expect.EfficiencyAdvisory);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("""{ "acceptedOutcomes": ["Clarify"] }""")]
    [InlineData("""{ "acceptedOutcomes": ["Clarify", "Clarify"] }""")]
    [InlineData("""{ "outcome": "Failed", "acceptedOutcomes": ["Complete", "Clarify"] }""")]
    [InlineData("""{ "scope": ["ActiveTab"], "preferredScope": ["ActiveTab"] }""")]
    [InlineData("""{ "scope": ["ActiveTab", "NewTaskTab"], "preferredScope": ["Direct"] }""")]
    public void InvalidAcceptedOutcomesOrPreferredScopeThrow(string expect)
    {
        var path = WriteTemp($$"""[ { "id": "a.b", "name": "n", "family": "f", "transcript": "x", "expect": {{expect}} } ]""");
        try { Assert.Throws<ScenarioLoadException>(() => ScenarioLoader.LoadFile(path)); }
        finally { File.Delete(path); }
    }
}
