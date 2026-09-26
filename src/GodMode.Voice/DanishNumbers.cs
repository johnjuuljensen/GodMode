namespace GodMode.Voice;

/// <summary>
/// Reads a whole number said in Danish words, as speech recognition may write it: "to hundrede og treogfirs" (283),
/// "tohundredeogtreogfirs", "et tusind og fem". Digits pass through ("283").
/// </summary>
public static class DanishNumbers
{
    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nul"] = 0, ["en"] = 1, ["et"] = 1, ["én"] = 1, ["ét"] = 1, ["to"] = 2, ["tre"] = 3, ["fire"] = 4, ["fem"] = 5,
        ["seks"] = 6, ["syv"] = 7, ["otte"] = 8, ["ni"] = 9, ["ti"] = 10, ["elleve"] = 11, ["tolv"] = 12,
        ["tretten"] = 13, ["fjorten"] = 14, ["femten"] = 15, ["seksten"] = 16, ["sytten"] = 17, ["atten"] = 18,
        ["nitten"] = 19, ["tyve"] = 20, ["tredive"] = 30, ["fyrre"] = 40, ["halvtreds"] = 50, ["tres"] = 60,
        ["halvfjerds"] = 70, ["firs"] = 80, ["halvfems"] = 90,
    };

    private const string Hundred = "hundrede";
    private const string Thousand = "tusind";

    /// <summary>The number, or null when the text is anything but one.</summary>
    public static int? Parse(string text)
    {
        var trimmed = text.Trim().TrimEnd('.', '!', '?', ',');
        if (trimmed.Length == 0) return null;
        if (trimmed.All(char.IsAsciiDigit))
            return int.TryParse(trimmed, out var digits) ? digits : null;

        var tokens = new List<string>();
        foreach (var word in trimmed.ToLowerInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Split(word) is not { } parts) return null;
            tokens.AddRange(parts);
        }
        if (tokens.Count == 0) return null;

        int total = 0, current = 0;
        foreach (var token in tokens)
        {
            switch (token)
            {
                case Hundred:
                    current = (current == 0 ? 1 : current) * 100;
                    break;
                case Thousand:
                    total += (current == 0 ? 1 : current) * 1000;
                    current = 0;
                    break;
                default:
                    current += Words[token];
                    break;
            }
        }
        return total + current;
    }

    /// <summary>
    /// A word as number words ("tohundredeogtreogfirs" → to, hundrede, tre, firs): "og" joins them and is dropped.
    /// Null when some of it is no number word.
    /// </summary>
    private static List<string>? Split(string word)
    {
        List<string> parts = [];
        var rest = word;
        while (rest.Length > 0)
        {
            if (rest.StartsWith("og", StringComparison.Ordinal))
            {
                rest = rest[2..];
                continue;
            }
            // Longest first, so "tretten" is not "tre" + "tten", nor "tres" "tre" + "s"
            var match = Words.Keys.Append(Hundred).Append(Thousand)
                .Where(w => rest.StartsWith(w, StringComparison.Ordinal))
                .MaxBy(w => w.Length);
            if (match is null) return null;
            parts.Add(match);
            rest = rest[match.Length..];
        }
        return parts;
    }
}
