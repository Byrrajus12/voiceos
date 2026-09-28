using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceOS.Eval.Live;

/// <summary>Entry point for `live` and `live summarize`. Parses args, loads/selects scenarios,
/// then hands off to LiveScenarioRunner (or aggregates existing results for summarize).</summary>
public static class LiveEvalCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        LiveEvalOptions options;
        try
        {
            options = LiveEvalOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            PrintUsage();
            return 1;
        }

        if (options.Mode == LiveEvalMode.Summarize)
            return Summarize(options.SummarizeFiles);

        var scenariosDir = options.ScenariosDir ?? Path.Combine(AppContext.BaseDirectory, "Scenarios");
        IReadOnlyList<Scenario> all;
        try
        {
            all = ScenarioLoader.LoadDirectory(scenariosDir);
        }
        catch (ScenarioLoadException ex)
        {
            Console.Error.WriteLine($"Scenario load error: {ex.Message}");
            return 1;
        }

        SelectionResult selection;
        try
        {
            selection = ScenarioSelector.Select(all, options.ScenarioIds, options.Tags, options.Families,
                options.All, options.IncludeUnsafe);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Selection error: {ex.Message}");
            return 1;
        }

        if (options.List)
        {
            PrintList(selection);
            return 0;
        }

        return await new LiveScenarioRunner().RunAsync(options, selection.Selected, selection.Skipped).ConfigureAwait(false);
    }

    private static int Summarize(IReadOnlyList<string> files)
    {
        var results = new List<ScenarioResult>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"Not found: {file}");
                return 1;
            }
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var result = JsonSerializer.Deserialize<ScenarioResult>(line, JsonOptions);
                if (result is not null) results.Add(result);
            }
        }
        var summary = EvalAggregator.Summarize(results);
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        return 0;
    }

    private static void PrintList(SelectionResult selection)
    {
        foreach (var s in selection.Selected)
            Console.WriteLine($"{s.Id}  family={s.Family} tags=[{string.Join(",", s.Tags)}] " +
                $"turns={s.Turns.Count} unattended={s.Safety.Unattended} mutatesExternalState={s.Safety.MutatesExternalState}");
        foreach (var s in selection.Skipped)
            Console.WriteLine($"{s.Scenario.Id}  SKIPPED: {s.Reason}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  live [--scenario <id>]... [--tag <t>]... [--family <f>]... [--all] [--list]");
        Console.WriteLine("       [--repeat N] [--include-unsafe] [--scenarios <dir>] [--out <dir>] [--verbose]");
        Console.WriteLine("  live summarize <results.jsonl>...");
    }
}
