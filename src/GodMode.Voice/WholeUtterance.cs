namespace GodMode.Voice;

/// <summary>An utterance that is a node's phrases alone (<see cref="HelpNode"/>, <see cref="DoneNode"/>), and nothing else.</summary>
public static class WholeUtterance
{
    /// <summary>
    /// The words as <paramref name="phrases"/>, one after another, the last of them perhaps only begun when
    /// <paramref name="open"/>: the index of the first, and whether each is of one word (one begun is not); null when any
    /// word is no part of one. At each word, the first phrase in the list that fits wins.
    /// </summary>
    public static (int First, bool OneWordEach)? Cover(string[] tokens, IReadOnlyList<string[]> phrases, bool open)
    {
        // From the end: the cover of the words from each position on, null where there is none
        var from = new (int First, bool OneWordEach)?[tokens.Length + 1];
        for (var at = tokens.Length - 1; at >= 0; at--)
        {
            for (var index = 0; index < phrases.Count; index++)
            {
                var phrase = phrases[index];
                var end = at + phrase.Length;
                if (end > tokens.Length)
                {
                    if (open && tokens.AsSpan(at).SequenceEqual(phrase.AsSpan(0, tokens.Length - at)))
                        from[at] = (index, false);
                }
                else if (tokens.AsSpan(at, phrase.Length).SequenceEqual(phrase))
                {
                    if (end == tokens.Length)
                        from[at] = (index, phrase.Length == 1);
                    else if (from[end] is { } rest)
                        from[at] = (index, phrase.Length == 1 && rest.OneWordEach);
                }
                if (from[at] is not null) break;
            }
        }
        return tokens.Length == 0 ? null : from[0];
    }
}
