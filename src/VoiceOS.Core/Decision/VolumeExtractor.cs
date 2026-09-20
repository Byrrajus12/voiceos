namespace VoiceOS.Core.Decision;

/// <summary>
/// Deterministic extraction of volume arguments from transcripts.
/// VoiceOS owns all value construction; no LLM-generated text reaches this layer.
/// </summary>
public static class VolumeExtractor
{
    private static readonly Dictionary<string, int> WordNumbers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = 0,   ["one"] = 1,    ["two"] = 2,     ["three"] = 3,   ["four"] = 4,
            ["five"] = 5,   ["six"] = 6,    ["seven"] = 7,   ["eight"] = 8,   ["nine"] = 9,
            ["ten"] = 10,   ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
            ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40,  ["fifty"] = 50,
            ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
            ["hundred"] = 100,
        };

    /// <summary>
    /// Extracts a volume percentage from the transcript.
    /// Supports digit strings ("15", "50") and word numbers ("fifteen", "fifty", "one hundred").
    /// Result is clamped to [0, 100].
    /// </summary>
    public static bool TryExtractPercent(string transcript, out int percent)
    {
        percent = 0;
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Digit strings take priority (most precise)
        foreach (var word in words)
        {
            var digits = new string(word.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out int n))
            {
                percent = Math.Clamp(n, 0, 100);
                return true;
            }
        }

        // Word numbers: accumulate tens + ones, handle hundred as multiplier.
        // "twenty five" → 20+5=25; "one hundred" → 1*100=100; "fifty" → 50.
        // Strip trailing punctuation so "fifteen." matches "fifteen".
        int accumulated = 0;
        bool found = false;
        foreach (var word in words)
        {
            var clean = word.TrimEnd('.', ',', '!', '?');
            if (!WordNumbers.TryGetValue(clean, out int val)) continue;
            found = true;
            if (val == 100)
                accumulated = accumulated == 0 ? 100 : accumulated * 100;
            else
                accumulated += val;
        }

        if (found)
        {
            percent = Math.Clamp(accumulated, 0, 100);
            return true;
        }

        return false;
    }
}
