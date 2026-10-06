namespace VoiceOS.Core.Browser;

/// <summary>
/// Separates a destination the user named from one a model merely thought sensible. Only a named
/// destination may constrain where VoiceOS goes; a suggestion is advisory and never binds a surface.
/// The test is the user's own words, not a per-service phrase list.
/// </summary>
public static class DestinationGrounding
{
    /// <summary>True when the utterance itself names <paramref name="name"/> (spoken split such as "you tube" included).</summary>
    public static bool Names(string? utterance, string? name)
    {
        var wanted = Compact(StripArticle(Words(name)));
        if (wanted.Length < 2 || string.IsNullOrWhiteSpace(utterance)) return false;
        var words = Words(utterance);
        for (var start = 0; start < words.Length; start++)
        {
            var joined = "";
            for (var end = start; end < Math.Min(words.Length, start + 4); end++)
            {
                joined += words[end];
                if (joined.Length > wanted.Length) break;
                if (joined == wanted) return true;
            }
        }
        return false;
    }

    /// <summary>True when any registered spelling of the service is in the utterance.</summary>
    public static bool Names(string? utterance, ServiceDescriptor? service)
        => service is not null && (Names(utterance, service.CanonicalName) || service.Aliases.Any(a => Names(utterance, a)));

    /// <summary>
    /// Removes every destination the compiler suggested that the user did not name: the preferred service and any
    /// Reach step to an unnamed site. A request with no named destination is a generic web task.
    /// </summary>
    public static CompiledBrowserTask Ground(CompiledBrowserTask compiled, string utterance, bool destinationKnown)
    {
        var normalization = compiled.Normalization;
        var serviceNamed = Names(utterance, normalization?.PreferredService)
            || Names(utterance, ServiceResolver.Resolve(normalization?.PreferredService));
        if (normalization is { PreferredService: not null } && !serviceNamed)
            normalization = normalization with { PreferredService = null, PreferredServiceUrl = null };
        var steps = compiled.Plan.Steps
            .Where(s => s.Kind != Interaction.PlanStepKind.Reach || destinationKnown || serviceNamed || Names(utterance, s.Target))
            .ToArray();
        if (steps.Length == compiled.Plan.Steps.Count && ReferenceEquals(normalization, compiled.Normalization)) return compiled;
        return steps.Length == 0 ? compiled with { Normalization = normalization }
            : new(compiled.Plan with { Steps = steps, CurrentStepIndex = 0 }, normalization);
    }

    private static string[] Words(string? text)
        => System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(static w => w.Length > 0).ToArray();

    private static string[] StripArticle(string[] words) => words.Length > 1 && words[0] == "the" ? words[1..] : words;
    private static string Compact(string[] words) => string.Concat(words);
}
