using System.Text.RegularExpressions;

namespace GodMode.Voice;

/// <summary>
/// A session's spoken topic (#455): two or three words of what it is about, said beside its handle so that a number is
/// never the only clue ("issue 283, mic-timeout"). Taken from its name, which is its issue's title for an issue
/// ("Issue #455: Voice: brief in words, never in context" is "brief in words"), or the name it was given ("chat_backup
/// job" is "backup job"). Handles stay what the user says to address a session; the topic is what the bot says with it.
/// </summary>
public static partial class ProjectTopics
{
    /// <summary>How many words of content a topic has at most; the small words between them ride along ("voice in the app").</summary>
    public const int MaxWords = 3;

    /// <summary>Words that carry nothing alone: a topic neither starts nor ends on one, and they do not count towards <see cref="MaxWords"/>.</summary>
    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "to", "for", "with", "by", "at", "from", "into", "is", "are", "be",
        "en", "et", "den", "det", "de", "og", "eller", "i", "på", "til", "for", "med", "af", "om", "fra", "er", "som", "at",
    };

    /// <summary>Branch and template prefixes a name is made with, which say its kind, not its topic.</summary>
    private static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "feat", "feature", "fix", "bug", "bugfix", "chore", "docs", "epic", "issue", "branch", "chat", "overseer", "experiment",
    };

    /// <summary>
    /// The topic of a session named <paramref name="name"/>, of <paramref name="kind"/>, said as <paramref name="label"/>:
    /// the first clause of its name, without its issue number, area tags ("Voice:") and kind prefixes, cut to
    /// <see cref="MaxWords"/> words of content. Null when nothing is left, or when the label says it all already
    /// ("master" of "branch master").
    /// </summary>
    public static string? Of(string name, string? kind, string label)
    {
        var text = name.Trim();
        // "Issue #455:", "#455 -", "455-": the number is the handle's
        text = IssueLead().Replace(text, "");
        // A slug ("283-mic-timeout", "chat_backup job") is words joined by its separators
        if (!text.Contains(' ') || text.Contains('_'))
            text = text.Replace('_', ' ').Replace('-', ' ');
        // Area tags before the title: "Voice: brief in words" is about "brief in words"
        while (AreaTag().Match(text) is { Success: true } tag && tag.Length < text.Length)
            text = text[tag.Length..];
        text = Clause().Split(text)[0];

        var words = Word().Matches(text).Select(m => m.Value).ToList();
        while (words.Count > 0 && (Small.Contains(words[0]) || Prefixes.Contains(words[0]) || Same(words[0], kind) || words[0].All(char.IsDigit)))
            words.RemoveAt(0);
        List<string> topic = [];
        var content = 0;
        foreach (var word in words)
        {
            if (!Small.Contains(word) && ++content > MaxWords) break;
            topic.Add(word);
        }
        while (topic.Count > 0 && (Small.Contains(topic[^1]) || topic[^1].All(char.IsDigit)))
            topic.RemoveAt(topic.Count - 1);

        // A topic of a letter or two says nothing, and one the label says already ("voice epics" of "epic voice") adds nothing
        var labelWords = Word().Matches(label).Select(m => Stem(m.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var said = topic.Where(w => !Small.Contains(w)).ToList();
        return said.Sum(w => w.Length) < MinLength || said.All(w => labelWords.Contains(Stem(w))) ? null : string.Join(' ', topic);
    }

    /// <summary>The fewest letters a topic's words have together: "b" or "x" is no topic.</summary>
    private const int MinLength = 3;

    /// <summary>A word without a plural's ending, so "epics" is "epic"'s word.</summary>
    private static string Stem(string word) =>
        word.Length > 3 && (word.EndsWith("er", StringComparison.OrdinalIgnoreCase) || word.EndsWith("es", StringComparison.OrdinalIgnoreCase)) ? word[..^2]
        : word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

    private static bool Same(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*(?:issue\s*)?#?\d+\s*[:\-–—.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex IssueLead();

    /// <summary>One or two words and a colon at the start: "Voice: ", "Epic: ", "Bug fix: ".</summary>
    [GeneratedRegex(@"^\s*[\p{L}\p{N}]+(?:\s[\p{L}\p{N}]+)?\s*:\s*")]
    private static partial Regex AreaTag();

    /// <summary>Where a title's first clause ends: punctuation, a dash between words, a bracket.</summary>
    [GeneratedRegex(@"[,;:.!?()\[\]""—–]|\s-\s")]
    private static partial Regex Clause();

    /// <summary>A word, hyphenated ones kept whole ("mic-timeout").</summary>
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
