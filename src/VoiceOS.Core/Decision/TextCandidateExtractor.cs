using System.Text.RegularExpressions;

namespace VoiceOS.Core.Decision;

public static class TextCandidateExtractor
{
    private static readonly Regex QuotedPattern =
        new(@"""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex PrefixPattern =
        new(@"^(?:type|search\s+for|search)\s+(.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts text candidates from a transcript.
    /// Priority: quoted text → prefixed patterns → fallback (whole utterance).
    /// Returns a dict keyed c0, c1, ... with actual text as values.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Extract(string transcript)
    {
        var candidates = new Dictionary<string, string>();
        int idx = 0;

        foreach (Match m in QuotedPattern.Matches(transcript))
        {
            var text = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(text))
                candidates[$"c{idx++}"] = text;
        }

        var prefix = PrefixPattern.Match(transcript);
        if (prefix.Success)
        {
            var text = prefix.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(text) && !candidates.ContainsValue(text))
                candidates[$"c{idx++}"] = text;
        }

        if (candidates.Count == 0)
            candidates["c0"] = transcript;

        return candidates;
    }
}
