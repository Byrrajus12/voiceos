using VoiceOS.Core.Decision;

namespace VoiceOS.Eval.Live;

/// <summary>Pure evaluation of one executed turn against its expectation. No product or I/O calls.</summary>
public static class ScenarioEvaluator
{
    public static TurnRecord EvaluateTurn(TurnRecord turn, Expectation? expect)
    {
        var expectedOutcome = expect?.PrimaryOutcome ?? ExpectedOutcome.Complete;
        var accepted = expect?.AllAcceptedOutcomes ?? [expectedOutcome];
        var checks = new List<CheckResult>();

        if (turn.OutcomeClass == OutcomeClass.Timeout)
        {
            checks.Add(Outcome(accepted, turn));
            return turn with { Checks = checks, Classification = Classification.Timeout };
        }
        if (turn.Failure is not null)
        {
            checks.Add(Outcome(accepted, turn));
            return turn with { Checks = checks, Classification = Classification.ExecutionFailure };
        }

        // An accepted outcome other than the preferred one is correct: it must still respect the
        // guards (no stray tabs, media commands, or excluded operations), and the preference miss
        // is recorded as advisory.
        if (!MatchesExpected(expectedOutcome, turn.OutcomeClass)
            && accepted.Any(o => MatchesExpected(o, turn.OutcomeClass)))
        {
            checks.Add(Outcome(accepted, turn));
            checks.Add(new("outcome.preference", CheckCategory.Efficiency, false,
                expectedOutcome.ToString(), turn.OutcomeClass.ToString()));
            var guards = EvaluateSideEffects(expect, turn, guardsOnly: true);
            checks.AddRange(guards);
            var alternateClass = guards.Any(c => !c.Passed) ? Classification.WrongAction
                : turn.OutcomeClass == OutcomeClass.Clarify ? Classification.CorrectClarify : Classification.Pass;
            return WithAdvisory(turn with { Checks = checks, Classification = alternateClass }, checks);
        }

        // Early, hard classifications from the outcome-expectation cross product that bypass
        // route/scope/side-effect checks entirely because the run never reached that stage meaningfully.
        Classification? early = null;
        var actual = turn.OutcomeClass;
        if (expectedOutcome == ExpectedOutcome.Clarify)
        {
            early = actual switch
            {
                OutcomeClass.Complete => Classification.MissedClarify,
                OutcomeClass.Unsupported => Classification.Unsupported,
                OutcomeClass.Failed => Classification.ExecutionFailure,
                _ => null // Clarify: provisional CorrectClarify, still subject to route/scope checks below.
            };
        }
        else if (expectedOutcome == ExpectedOutcome.Complete)
        {
            early = actual switch
            {
                OutcomeClass.Clarify => Classification.UnnecessaryClarify,
                OutcomeClass.Unsupported => Classification.Unsupported,
                _ => null
            };
        }

        checks.Add(Outcome(accepted, turn));

        if (early is Classification.MissedClarify or Classification.Unsupported or Classification.ExecutionFailure
            or Classification.UnnecessaryClarify)
            return turn with { Checks = checks, Classification = early.Value };

        var provisionalClarify = expectedOutcome == ExpectedOutcome.Clarify && actual == OutcomeClass.Clarify;

        var routeCheck = EvaluateRoute(expect, turn);
        if (routeCheck is not null) checks.Add(routeCheck);
        var scopeCheck = EvaluateScope(expect, turn);
        if (scopeCheck is not null) checks.Add(scopeCheck);
        var sideEffectChecks = EvaluateSideEffects(expect, turn);
        checks.AddRange(sideEffectChecks);
        var endStateChecks = EvaluateEndState(expect, turn, out var unchangedWindowChecks);
        checks.AddRange(endStateChecks);
        var efficiencyChecks = EvaluateEfficiency(expect, turn);
        checks.AddRange(efficiencyChecks);
        // Budgets are correctness-neutral when the expectation marks efficiency advisory.
        var blockingEfficiency = expect?.EfficiencyAdvisory == true ? [] : efficiencyChecks;
        if (scopeCheck is { Passed: true } && expect?.PreferredScope is { Count: > 0 } preferred)
        {
            var actualScope = ToScopeExpectation(turn.Scope);
            checks.Add(new("scope.preference", CheckCategory.Efficiency,
                actualScope is { } scopeValue && preferred.Contains(scopeValue),
                string.Join("|", preferred), actualScope?.ToString() ?? "(none)"));
        }

        Classification classification;
        if (routeCheck is { Passed: false })
            classification = Classification.WrongRoute;
        else if (scopeCheck is { Passed: false })
            classification = Classification.WrongScope;
        else if (actual == OutcomeClass.Failed && expectedOutcome == ExpectedOutcome.Complete)
            classification = Classification.ExecutionFailure;
        else if (sideEffectChecks.Any(c => !c.Passed))
            classification = Classification.WrongAction;
        else if (endStateChecks.Any(c => !c.Passed))
            classification = actual != OutcomeClass.Complete
                ? Classification.ExecutionFailure
                : AllUnchanged(endStateChecks, turn, unchangedWindowChecks) ? Classification.FalseSuccess : Classification.WrongTarget;
        else if (expectedOutcome is ExpectedOutcome.Failed or ExpectedOutcome.Unsupported
            && !MatchesExpected(expectedOutcome, actual))
            classification = Classification.UnexpectedOutcome;
        else if (blockingEfficiency.Any(c => !c.Passed))
            classification = Classification.Partial;
        else
            classification = provisionalClarify ? Classification.CorrectClarify : Classification.Pass;

        return WithAdvisory(turn with { Checks = checks, Classification = classification }, checks);
    }

    /// <summary>Records failed efficiency/preference checks of a correct turn separately from correctness.</summary>
    private static TurnRecord WithAdvisory(TurnRecord turn, IReadOnlyList<CheckResult> checks)
        => ScenarioResult.SuccessSet.Contains(turn.Classification)
            ? turn with { EfficiencyMisses = checks.Where(c => c.Category == CheckCategory.Efficiency && !c.Passed)
                .Select(c => c.Name).ToArray() }
            : turn;

    /// <summary>First non-success turn's classification; if all succeeded, the last turn's.</summary>
    public static Classification EvaluateScenario(IReadOnlyList<TurnRecord> turns)
    {
        if (turns.Count == 0) return Classification.HarnessError;
        var firstFailure = turns.FirstOrDefault(t => !ScenarioResult.SuccessSet.Contains(t.Classification));
        return firstFailure?.Classification ?? turns[^1].Classification;
    }

    private static bool MatchesExpected(ExpectedOutcome expected, OutcomeClass actual) => expected switch
    {
        ExpectedOutcome.Complete => actual == OutcomeClass.Complete,
        ExpectedOutcome.Clarify => actual == OutcomeClass.Clarify,
        ExpectedOutcome.Unsupported => actual == OutcomeClass.Unsupported,
        ExpectedOutcome.Failed => actual == OutcomeClass.Failed,
        _ => false
    };

    private static CheckResult Outcome(IReadOnlyList<ExpectedOutcome> accepted, TurnRecord turn)
        => new("outcome", CheckCategory.Outcome, accepted.Any(o => MatchesExpected(o, turn.OutcomeClass)),
            string.Join("|", accepted), turn.OutcomeClass.ToString());

    private static CheckResult? EvaluateRoute(Expectation? expect, TurnRecord turn)
    {
        if (expect?.Route is not { Count: > 0 } routes) return null;
        var actual = turn.Route?.Route;
        var passed = actual is not null && routes.Contains(actual.Value);
        return new("route", CheckCategory.Route, passed, string.Join("|", routes), actual?.ToString() ?? "(none)");
    }

    private static ScopeExpectation? ToScopeExpectation(ScopeInfo? scope)
    {
        if (scope is null) return null;
        return scope.Kind switch
        {
            VoiceOS.Core.Browser.ExecutionScopeKind.DirectCapability => ScopeExpectation.Direct,
            VoiceOS.Core.Browser.ExecutionScopeKind.Browser => scope.BrowserKind switch
            {
                VoiceOS.Core.Browser.BrowserScopeKind.ActiveTab => ScopeExpectation.ActiveTab,
                VoiceOS.Core.Browser.BrowserScopeKind.ExistingNamedTab => ScopeExpectation.ExistingNamedTab,
                VoiceOS.Core.Browser.BrowserScopeKind.RecentOwnedTaskTab => ScopeExpectation.RecentOwnedTaskTab,
                VoiceOS.Core.Browser.BrowserScopeKind.NewTaskTab => ScopeExpectation.NewTaskTab,
                _ => null
            },
            VoiceOS.Core.Browser.ExecutionScopeKind.NativeInteraction => ScopeExpectation.Native,
            VoiceOS.Core.Browser.ExecutionScopeKind.TextTransform => ScopeExpectation.TextTransform,
            VoiceOS.Core.Browser.ExecutionScopeKind.Clarify => ScopeExpectation.Clarify,
            _ => null
        };
    }

    private static CheckResult? EvaluateScope(Expectation? expect, TurnRecord turn)
    {
        if (expect?.Scope is not { Count: > 0 } scopes) return null;
        var actual = ToScopeExpectation(turn.Scope);
        var passed = actual is not null && scopes.Contains(actual.Value);
        return new("scope", CheckCategory.Scope, passed, string.Join("|", scopes), actual?.ToString() ?? "(none)");
    }

    /// <summary>Side-effect checks. With <paramref name="guardsOnly"/>, only the checks that forbid
    /// something (excluded steps/operations, media commands, too many new tabs) apply: they hold for
    /// every accepted outcome, while positive checks describe the preferred outcome only.</summary>
    private static List<CheckResult> EvaluateSideEffects(Expectation? expect, TurnRecord turn, bool guardsOnly = false)
    {
        var results = new List<CheckResult>();
        if (expect is null) return results;
        if (expect.DirectSteps is { } ds)
        {
            var steps = turn.Execution.ProgramSteps;
            if (!guardsOnly && ds.Include is { Count: > 0 } include)
            {
                var missing = include.Where(s => !steps.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
                results.Add(new("directSteps.include", CheckCategory.SideEffect, missing.Length == 0,
                    string.Join(",", include), string.Join(",", steps)));
            }
            if (ds.Exclude is { Count: > 0 } exclude)
            {
                var present = exclude.Where(s => steps.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
                results.Add(new("directSteps.exclude", CheckCategory.SideEffect, present.Length == 0,
                    string.Join(",", exclude), string.Join(",", steps)));
            }
        }
        if (expect.BrowserOperations is { } bo)
        {
            var ops = turn.Execution.BrowserActivities.Select(a => a.Operation ?? "").ToArray();
            if (!guardsOnly && bo.Include is { Count: > 0 } include)
            {
                var missing = include.Where(o => !ops.Contains(o, StringComparer.OrdinalIgnoreCase)).ToArray();
                results.Add(new("browserOperations.include", CheckCategory.SideEffect, missing.Length == 0,
                    string.Join(",", include), string.Join(",", ops)));
            }
            if (bo.Exclude is { Count: > 0 } exclude)
            {
                var present = exclude.Where(o => ops.Contains(o, StringComparer.OrdinalIgnoreCase)).ToArray();
                results.Add(new("browserOperations.exclude", CheckCategory.SideEffect, present.Length == 0,
                    string.Join(",", exclude), string.Join(",", ops)));
            }
        }
        if (expect.NoMediaCommand == true)
        {
            var hasMedia = turn.Execution.ProgramSteps.Contains("MediaControl", StringComparer.OrdinalIgnoreCase);
            results.Add(new("noMediaCommand", CheckCategory.SideEffect, !hasMedia, "false",
                hasMedia.ToString()));
        }
        if (expect.NewTabs is { } guardTabs && guardsOnly)
        {
            var created = turn.Counts.TabsCreated;
            results.Add(new("newTabs", CheckCategory.SideEffect, guardTabs.Max is not { } max || created <= max,
                $"[,{guardTabs.Max}]", created.ToString()));
        }
        if (guardsOnly) return results;
        var attempted = turn.Execution.AttemptedDirectSteps ?? [];
        if (expect.MediaOperation is { } op)
        {
            // Every attempted transport step must be the requested operation, and at least one must exist.
            var ops = attempted.Where(static s => s.MediaOperation is not null).Select(static s => s.MediaOperation!.Value).ToArray();
            results.Add(new("mediaOperation", CheckCategory.SideEffect, ops.Length > 0 && ops.All(o => o == op),
                op.ToString(), ops.Length == 0 ? "(none)" : string.Join(",", ops)));
        }
        if (expect.SetVolume is { } level)
        {
            var levels = attempted.Where(static s => s.VolumeValue is not null).Select(static s => s.VolumeValue!.Value).ToArray();
            results.Add(new("setVolume", CheckCategory.SideEffect, levels.Length > 0 && levels.All(v => v == level),
                level.ToString(), levels.Length == 0 ? "(none)" : string.Join(",", levels)));
        }
        if (expect.AdjustVolume is { } adjust)
        {
            var adjustments = attempted.Where(static s => s.VolumeDirection is not null).ToArray();
            var passed = adjustments.Length > 0 && adjustments.All(s => s.VolumeDirection == adjust.Direction
                && (adjust.Amount is null || s.VolumeAmount == adjust.Amount));
            results.Add(new("adjustVolume", CheckCategory.SideEffect, passed,
                $"{adjust.Direction}{(adjust.Amount is { } amount ? $" {amount}" : "")}",
                adjustments.Length == 0 ? "(none)"
                    : string.Join(",", adjustments.Select(static s => $"{s.VolumeDirection}{(s.VolumeAmount is { } a ? $" {a}" : "")}"))));
        }
        if (expect.NewTabs is { } nt)
        {
            var created = turn.Counts.TabsCreated;
            var passed = (nt.Min is not { } min || created >= min) && (nt.Max is not { } max || created <= max);
            results.Add(new("newTabs", CheckCategory.SideEffect, passed,
                $"[{nt.Min},{nt.Max}]", created.ToString()));
        }
        return results;
    }

    private static List<CheckResult> EvaluateEndState(Expectation? expect, TurnRecord turn,
        out HashSet<string> unchangedWindowChecks)
    {
        var results = new List<CheckResult>();
        unchangedWindowChecks = [];
        var f = expect?.Final;
        if (f is null) return results;
        for (var i = 0; i < (f.Windows?.Count ?? 0); i++)
        {
            var (check, unchanged) = WindowExpectationEvaluator.Evaluate(f.Windows![i], i, turn.Initial, turn.Final);
            results.Add(check);
            if (unchanged) unchangedWindowChecks.Add(check.Name);
        }
        if (f.ForegroundProcess is { } fp)
            results.Add(new("final.foregroundProcess", CheckCategory.EndState,
                Contains(turn.Final.Foreground?.ProcessName, fp), fp, turn.Final.Foreground?.ProcessName));
        if (f.ForegroundTitleContains is { } ftc)
            results.Add(new("final.foregroundTitleContains", CheckCategory.EndState,
                Contains(turn.Final.Foreground?.Title, ftc), ftc, turn.Final.Foreground?.Title));
        if (f.ActiveTabOriginContains is { } aoc)
            results.Add(new("final.activeTabOriginContains", CheckCategory.EndState,
                Contains(turn.Final.ActiveTab?.Origin, aoc), aoc, turn.Final.ActiveTab?.Origin));
        if (f.ActiveTabUrlContains is { } auc)
            results.Add(new("final.activeTabUrlContains", CheckCategory.EndState,
                Contains(turn.Final.ActiveTab?.Url, auc), auc, turn.Final.ActiveTab?.Url));
        if (f.ActiveTabTitleContains is { } atc)
            results.Add(new("final.activeTabTitleContains", CheckCategory.EndState,
                Contains(turn.Final.ActiveTab?.Title, atc), atc, turn.Final.ActiveTab?.Title));
        if (f.LastActivatedTargetContains is { } lat)
        {
            // The control whose activation produced the end state, for results that live in in-page
            // state (an SPA menu at an unchanged URL) rather than in the URL.
            var last = turn.Execution.BrowserActivities.LastOrDefault(static a => a.Operation == "Activate")?.TargetName;
            results.Add(new("final.lastActivatedTargetContains", CheckCategory.EndState, Contains(last, lat), lat, last));
        }
        return results;
    }

    private static bool Contains(string? actual, string expectedSubstring)
        => actual is not null && actual.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when every failing end-state property observed no change between initial and final —
    /// the product claimed success but nothing actually happened.</summary>
    private static bool AllUnchanged(IEnumerable<CheckResult> endStateChecks, TurnRecord turn,
        IReadOnlySet<string> unchangedWindowChecks)
    {
        foreach (var check in endStateChecks.Where(c => !c.Passed))
        {
            var unchanged = check.Name switch
            {
                _ when check.Name.StartsWith("final.windows[", StringComparison.Ordinal)
                    => unchangedWindowChecks.Contains(check.Name),
                "final.foregroundProcess" or "final.foregroundTitleContains" =>
                    string.Equals(turn.Initial.Foreground?.ProcessName, turn.Final.Foreground?.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(turn.Initial.Foreground?.Title, turn.Final.Foreground?.Title, StringComparison.OrdinalIgnoreCase),
                "final.activeTabOriginContains" or "final.activeTabUrlContains" or "final.activeTabTitleContains" =>
                    string.Equals(turn.Initial.ActiveTab?.Url, turn.Final.ActiveTab?.Url, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(turn.Initial.ActiveTab?.Title, turn.Final.ActiveTab?.Title, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!unchanged) return false;
        }
        return true;
    }

    private static List<CheckResult> EvaluateEfficiency(Expectation? expect, TurnRecord turn)
    {
        var results = new List<CheckResult>();
        if (expect?.MaxActions is { } maxActions)
            results.Add(new("maxActions", CheckCategory.Efficiency, turn.Counts.Actions <= maxActions,
                maxActions.ToString(), turn.Counts.Actions.ToString()));
        if (expect?.MaxBrowserDecisions is { } maxDecisions)
            results.Add(new("maxBrowserDecisions", CheckCategory.Efficiency, turn.Counts.BrowserDecisions <= maxDecisions,
                maxDecisions.ToString(), turn.Counts.BrowserDecisions.ToString()));
        return results;
    }
}
