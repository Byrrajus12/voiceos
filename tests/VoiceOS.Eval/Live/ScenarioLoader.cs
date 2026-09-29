using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;

namespace VoiceOS.Eval.Live;

/// <summary>Loads and validates scenario JSON files into the normalized <see cref="Scenario"/> model.</summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelled field would otherwise silently drop an expectation.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<Scenario> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new ScenarioLoadException($"Scenario directory not found: {directory}");
        var scenarios = new List<Scenario>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var scenario in LoadFile(file))
            {
                if (seenIds.TryGetValue(scenario.Id, out var existingFile))
                    throw new ScenarioLoadException(
                        $"Duplicate scenario id '{scenario.Id}' in {Path.GetFileName(file)} (already defined in {Path.GetFileName(existingFile)})");
                seenIds[scenario.Id] = file;
                scenarios.Add(scenario);
            }
        }
        return scenarios;
    }

    public static IReadOnlyList<Scenario> LoadFile(string path)
    {
        var fileName = Path.GetFileName(path);
        List<RawScenario>? raw;
        try
        {
            var json = File.ReadAllText(path);
            raw = JsonSerializer.Deserialize<List<RawScenario>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ScenarioLoadException($"{fileName}: invalid JSON — {ex.Message}");
        }
        if (raw is null) return [];
        var result = new List<Scenario>();
        foreach (var r in raw)
            result.Add(Normalize(r, fileName));
        return result;
    }

    private static Scenario Normalize(RawScenario r, string fileName)
    {
        var id = r.Id ?? throw new ScenarioLoadException($"{fileName}: scenario missing required 'id'");
        void Fail(string message) => throw new ScenarioLoadException($"{fileName}: scenario '{id}': {message}");

        if (string.IsNullOrWhiteSpace(r.Name)) Fail("missing required 'name'");
        if (string.IsNullOrWhiteSpace(r.Family)) Fail("missing required 'family'");

        var hasTurns = r.Turns is { Count: > 0 };
        var hasShorthand = r.Transcript is not null;
        if (!hasTurns && !hasShorthand)
            Fail("must specify either 'turns' or a shorthand 'transcript'");

        List<Turn> turns;
        if (hasTurns)
            turns = r.Turns!.Select(t => NormalizeTurn(t, fileName, id, r.SettleMs ?? 800)).ToList();
        else
            turns = [new Turn(r.Transcript!, NormalizeExpectation(r.Expect, fileName, id), r.SettleMs ?? 800)];

        var safety = r.Safety is null ? new ScenarioSafety()
            : new ScenarioSafety(r.Safety.Unattended ?? true, r.Safety.MutatesExternalState ?? false, r.Safety.Note);

        var preconditions = (r.Preconditions ?? []).Select(p => NormalizePrecondition(p, fileName, id)).ToList();
        var setup = (r.Setup ?? []).Select(s => NormalizeSetupStep(s, fileName, id)).ToList();
        var cleanup = (r.Cleanup ?? []).Select(s => NormalizeSetupStep(s, fileName, id)).ToList();

        return new Scenario(id, r.Name!, r.Family!, r.Tags ?? [], safety, preconditions, setup, cleanup,
            turns, r.TimeoutSeconds ?? 60, r.SettleMs ?? 800);
    }

    private static Turn NormalizeTurn(RawTurn t, string fileName, string id, int scenarioSettleMs)
    {
        if (string.IsNullOrWhiteSpace(t.Transcript))
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': turn missing 'transcript'");
        return new Turn(t.Transcript, NormalizeExpectation(t.Expect, fileName, id), t.SettleMs ?? scenarioSettleMs,
            t.Before?.Select(s => NormalizeSetupStep(s, fileName, id)).ToList());
    }

    private static Expectation? NormalizeExpectation(RawExpectation? e, string fileName, string id)
    {
        if (e is null) return null;
        if (e.MediaOperation is { } op && !Enum.IsDefined(op))
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': unknown mediaOperation '{op}'");
        if (e.SetVolume is < 0 or > 100)
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': setVolume must be 0-100");
        if (e.AcceptedOutcomes is { } accepted)
        {
            if (accepted.Count < 2 || accepted.Distinct().Count() != accepted.Count)
                throw new ScenarioLoadException($"{fileName}: scenario '{id}': acceptedOutcomes needs two or more distinct outcomes");
            if (e.Outcome is { } preferred && !accepted.Contains(preferred))
                throw new ScenarioLoadException($"{fileName}: scenario '{id}': outcome '{preferred}' must be one of acceptedOutcomes");
        }
        if (e.PreferredScope is { Count: > 0 } preferredScope
            && (e.Scope is not { Count: > 1 } scope || preferredScope.Any(p => !scope.Contains(p))))
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': preferredScope must be a subset of a multi-valued scope");
        return new Expectation(
            e.Outcome, e.Route, e.Scope,
            e.DirectSteps is null ? null : new StringSetExpectation(e.DirectSteps.Include, e.DirectSteps.Exclude),
            e.BrowserOperations is null ? null : new StringSetExpectation(e.BrowserOperations.Include, e.BrowserOperations.Exclude),
            e.NoMediaCommand, e.NewTabs is null ? null : new NewTabsExpectation(e.NewTabs.Min, e.NewTabs.Max),
            e.MaxActions, e.MaxBrowserDecisions,
            e.Final is null ? null : new FinalExpectation(e.Final.ForegroundProcess, e.Final.ForegroundTitleContains,
                e.Final.ActiveTabOriginContains, e.Final.ActiveTabUrlContains, e.Final.ActiveTabTitleContains,
                e.Final.Windows?.Select(NormalizeWindow).ToList(), e.Final.LastActivatedTargetContains),
            e.MediaOperation, e.SetVolume, NormalizeAdjustVolume(e.AdjustVolume),
            e.AcceptedOutcomes, e.PreferredScope, e.EfficiencyAdvisory ?? false);

        WindowExpectation NormalizeWindow(RawWindow w)
        {
            if (w.Process is null && w.TitleContains is null && w.InitialForeground != true)
                Fail("a window expectation needs 'process', 'titleContains', or 'initialForeground'");
            if (w.Snapped is { } side && !Enum.IsDefined(side)) Fail("window 'snapped' must be Left or Right");
            var exists = w.Exists ?? true;
            if (!exists && (w.IsNew == true || w.Minimized is not null || w.Maximized is not null
                    || w.Snapped is not null || w.MonitorChanged == true))
                Fail("an absent window ('exists': false) cannot also assert window state");
            return new(w.Process, w.TitleContains, w.InitialForeground ?? false, exists, w.IsNew ?? false,
                w.Minimized, w.Maximized, w.Snapped, w.MonitorChanged ?? false);
        }

        // The enum converter also accepts integers, so reject values outside the defined set.
        void Fail(string message) => throw new ScenarioLoadException($"{fileName}: scenario '{id}': {message}");
        AdjustVolumeExpectation? NormalizeAdjustVolume(RawAdjustVolume? a)
        {
            if (a is null) return null;
            if (a.Direction is not { } direction || !Enum.IsDefined(direction)) Fail("adjustVolume requires a valid 'direction' (Up|Down)");
            if (a.Amount is < 1 or > 100) Fail("adjustVolume 'amount' must be 1-100");
            return new(a.Direction!.Value, a.Amount);
        }
    }

    private static Precondition NormalizePrecondition(RawPrecondition p, string fileName, string id)
    {
        if (string.IsNullOrWhiteSpace(p.Kind))
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': precondition missing 'kind'");
        var kind = p.Kind switch
        {
            "companionConnected" => PreconditionKind.CompanionConnected,
            "appInstalled" => PreconditionKind.AppInstalled,
            "foregroundProcess" => PreconditionKind.ForegroundProcess,
            "windowOpen" => PreconditionKind.WindowOpen,
            "browserTabs" => PreconditionKind.BrowserTabs,
            "activeTabOrigin" => PreconditionKind.ActiveTabOrigin,
            "minMonitors" => PreconditionKind.MinMonitors,
            _ => throw new ScenarioLoadException($"{fileName}: scenario '{id}': unknown precondition kind '{p.Kind}'")
        };
        return new Precondition(kind, p.WaitSeconds ?? 40, p.App, p.Process, p.Min ?? 0, p.Contains, p.Count ?? 0);
    }

    private static SetupStep NormalizeSetupStep(RawSetupStep s, string fileName, string id)
    {
        if (string.IsNullOrWhiteSpace(s.Kind))
            throw new ScenarioLoadException($"{fileName}: scenario '{id}': setup/cleanup step missing 'kind'");
        var kind = s.Kind switch
        {
            "startProcess" => SetupStepKind.StartProcess,
            "focusWindow" => SetupStepKind.FocusWindow,
            "wait" => SetupStepKind.Wait,
            "closeNewTabs" => SetupStepKind.CloseNewTabs,
            "closeFixtureTab" => SetupStepKind.CloseFixtureTab,
            _ => throw new ScenarioLoadException($"{fileName}: scenario '{id}': unknown setup step kind '{s.Kind}'")
        };
        return new SetupStep(kind, s.File, s.Args, s.Process, s.Ms ?? 0);
    }

    // ── Raw JSON DTOs (nullable, unvalidated) ────────────────────────────────────
    private sealed record RawScenario(string? Id, string? Name, string? Family, List<string>? Tags,
        RawSafety? Safety, List<RawPrecondition>? Preconditions, List<RawSetupStep>? Setup,
        List<RawSetupStep>? Cleanup, List<RawTurn>? Turns, string? Transcript, RawExpectation? Expect,
        int? TimeoutSeconds, int? SettleMs);

    private sealed record RawSafety(bool? Unattended, bool? MutatesExternalState, string? Note);

    private sealed record RawPrecondition(string? Kind, int? WaitSeconds, string? App, string? Process,
        int? Min, string? Contains, int? Count);

    private sealed record RawSetupStep(string? Kind, string? File, string? Args, string? Process, int? Ms);

    private sealed record RawTurn(string? Transcript, RawExpectation? Expect, int? SettleMs, List<RawSetupStep>? Before = null);

    private sealed record RawExpectation(ExpectedOutcome? Outcome, List<CommandRoute>? Route,
        List<ScopeExpectation>? Scope, RawStringSet? DirectSteps, RawStringSet? BrowserOperations,
        bool? NoMediaCommand, RawNewTabs? NewTabs, int? MaxActions, int? MaxBrowserDecisions, RawFinal? Final,
        MediaOperation? MediaOperation, int? SetVolume, RawAdjustVolume? AdjustVolume,
        List<ExpectedOutcome>? AcceptedOutcomes, List<ScopeExpectation>? PreferredScope, bool? EfficiencyAdvisory);

    private sealed record RawAdjustVolume(VolumeDirection? Direction, int? Amount);

    private sealed record RawStringSet(List<string>? Include, List<string>? Exclude);
    private sealed record RawNewTabs(int? Min, int? Max);
    private sealed record RawFinal(string? ForegroundProcess, string? ForegroundTitleContains,
        string? ActiveTabOriginContains, string? ActiveTabUrlContains, string? ActiveTabTitleContains,
        List<RawWindow>? Windows, string? LastActivatedTargetContains);

    private sealed record RawWindow(string? Process, string? TitleContains, bool? InitialForeground, bool? Exists,
        bool? IsNew, bool? Minimized, bool? Maximized, SnapSide? Snapped, bool? MonitorChanged);
}
