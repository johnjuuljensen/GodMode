using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace GodMode.Voice;

/// <summary>
/// Which of Danish and English the user spoke a final in (#507), so the code's words follow a switch of language as the
/// model's do (<see cref="VoicePhrases.Heard"/>). Told by the small words each language has and the other has not, and
/// by æ, ø and å: the English terms of GodMode's work inside a Danish sentence ("svar at den skal merge the branch") are
/// outweighed by its Danish words, and a final of no such words ("status 283", "pull request") tells nothing.
/// </summary>
/// <remarks>
/// The transcriber hears Danish as English now and then ("Nej, som overseer" as "Now as overseer", #449), a few words at
/// most: so a switch away from the session's language takes a whole sentence of the other (<see cref="SwitchAway"/>), and
/// one back to it a word (<see cref="VoicePhrases.Heard"/>).
/// </remarks>
public static partial class SpokenLanguage
{
    // Words of one language that are not words of the other ("i", "to", "at", "for", "men", "her" are both). Nor "er"
    // and "min" (#523): English says them too, a filler ("er, status 283") and minutes ("30 min")
    private static readonly FrozenSet<string> DanishWords = FrozenSet.ToFrozenSet(
    [
        "hvad", "hvilke", "hvilken", "hvorfor", "hvordan", "hvor", "venter", "svar", "svarede", "sig", "og", "det", "der",
        "jeg", "du", "dig", "mig", "den", "til", "ikke", "mere", "med", "skal", "kan", "har", "om", "af", "en", "et", "nyt",
        "siden", "sidst", "sidste", "spurgte", "spørger", "alle", "gamle", "stille", "marker", "som", "vigtig", "hjælp", "nej",
        "ja", "tak", "godt", "lige", "nu", "så", "eller", "også", "hele", "igen", "projekter", "projekt", "kører", "noget",
        "videre", "læst", "læs", "opret", "diktér", "dikter", "hvem", "være", "var", "blev", "din", "sin", "fra",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> EnglishWords = FrozenSet.ToFrozenSet(
    [
        "what", "which", "why", "how", "where", "needs", "need", "me", "read", "reply", "replied", "say", "tell", "the", "is",
        "are", "and", "of", "it", "its", "more", "new", "since", "last", "asked", "all", "old", "quiet", "seen", "mark", "as",
        "important", "help", "no", "yes", "thanks", "please", "now", "but", "or", "also", "whole", "again", "asks", "that",
        "this", "there", "running", "waiting", "does", "do", "did", "has", "have", "was", "were", "can", "will", "should",
        "my", "you", "your", "about", "with", "on", "in", "a", "an", "projects", "project", "dictate", "create", "who",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>How far ahead of the session's language the other's words must be in a final to switch to it: a sentence's worth.</summary>
    public const int SwitchAway = 3;

    /// <summary>How Danish the final is: its Danish words less its English ones; below zero for English, zero when it tells neither.</summary>
    public static int Lean(string text)
    {
        var (danish, english) = (0, 0);
        foreach (var word in Words().Matches(text).Select(m => m.Value))
        {
            if (DanishWords.Contains(word) || word.AsSpan().IndexOfAny("æøåÆØÅ") >= 0) danish++;
            else if (EnglishWords.Contains(word)) english++;
        }
        return danish - english;
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
