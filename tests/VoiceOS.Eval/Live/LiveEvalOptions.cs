namespace VoiceOS.Eval.Live;

public enum LiveEvalMode { Run, Summarize }

/// <summary>Parsed, validated CLI options for `live` and `live summarize`. Pure parsing — no I/O.</summary>
public sealed record LiveEvalOptions(
    LiveEvalMode Mode,
    IReadOnlyList<string> ScenarioIds,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Families,
    bool All,
    bool List,
    int Repeat,
    bool IncludeUnsafe,
    string? ScenariosDir,
    string? OutDir,
    bool Verbose,
    IReadOnlyList<string> SummarizeFiles)
{
    public static LiveEvalOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "summarize")
        {
            var files = args.Skip(1).ToList();
            if (files.Count == 0)
                throw new ArgumentException("`live summarize` requires at least one results.jsonl path.");
            return new(LiveEvalMode.Summarize, [], [], [], false, false, 1, false, null, null, false, files);
        }

        var ids = new List<string>();
        var tags = new List<string>();
        var families = new List<string>();
        var all = false;
        var list = false;
        var repeat = 1;
        var includeUnsafe = false;
        string? scenariosDir = null;
        string? outDir = null;
        var verbose = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--scenario": ids.Add(Next(args, ref i, "--scenario")); break;
                case "--tag": tags.Add(Next(args, ref i, "--tag")); break;
                case "--family": families.Add(Next(args, ref i, "--family")); break;
                case "--all": all = true; break;
                case "--list": list = true; break;
                case "--repeat": repeat = int.Parse(Next(args, ref i, "--repeat")); break;
                case "--include-unsafe": includeUnsafe = true; break;
                case "--scenarios": scenariosDir = Next(args, ref i, "--scenarios"); break;
                case "--out": outDir = Next(args, ref i, "--out"); break;
                case "--verbose": verbose = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        if (repeat < 1) throw new ArgumentException("--repeat must be at least 1.");
        return new(LiveEvalMode.Run, ids, tags, families, all, list, repeat, includeUnsafe, scenariosDir, outDir,
            verbose, []);
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"{flag} requires a value.");
        return args[++i];
    }
}
