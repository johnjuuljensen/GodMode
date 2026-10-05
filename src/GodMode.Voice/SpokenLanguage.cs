using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace GodMode.Voice;

/// <summary>
/// Which of Danish and English the user spoke a final in (#507), so the code's words follow a switch of language as the
/// model's do (<see cref="VoicePhrases.Heard"/>). Told by the small words each language has and the other has not, and
/// by æ, ø and å: the English terms of GodMode's work inside a Danish sentence ("svar at den skal merge the branch") are
/// outweighed by its Danish words, and a final of no such words ("status 283", "pull request") tells nothing.
/// </summary>
public static partial class SpokenLanguage
{
    // Words of one language that are not words of the other ("i", "to", "at", "for", "men", "her" are both)
    private static readonly FrozenSet<string> DanishWords = FrozenSet.ToFrozenSet(
    [
        "hvad", "hvilke", "hvilken", "hvorfor", "hvordan", "hvor", "venter", "svar", "svarede", "sig", "og", "er", "det", "der",
        "jeg", "du", "dig", "mig", "den", "til", "ikke", "mere", "med", "skal", "kan", "har", "om", "af", "en", "et", "nyt",
        "siden", "sidst", "sidste", "spurgte", "spørger", "alle", "gamle", "stille", "marker", "som", "vigtig", "hjælp", "nej",
        "ja", "tak", "godt", "lige", "nu", "så", "eller", "også", "hele", "igen", "projekter", "projekt", "kører", "noget",
        "videre", "læst", "læs", "opret", "diktér", "dikter", "hvem", "være", "var", "blev", "min", "din", "sin", "fra",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> EnglishWords = FrozenSet.ToFrozenSet(
    [
        "what", "which", "why", "how", "where", "needs", "need", "me", "read", "reply", "replied", "say", "tell", "the", "is",
        "are", "and", "of", "it", "its", "more", "new", "since", "last", "asked", "all", "old", "quiet", "seen", "mark", "as",
        "important", "help", "no", "yes", "thanks", "please", "now", "but", "or", "also", "whole", "again", "asks", "that",
        "this", "there", "running", "waiting", "does", "do", "did", "has", "have", "was", "were", "can", "will", "should",
        "my", "you", "your", "about", "with", "on", "in", "a", "an", "projects", "project", "dictate", "create", "who",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>True for Danish, false for English, null when the final tells neither apart from the other.</summary>
    public static bool? IsDanish(string text)
    {
        var (danish, english) = (0, 0);
        foreach (var word in Words().Matches(text).Select(m => m.Value))
        {
            if (DanishWords.Contains(word) || word.AsSpan().IndexOfAny("æøåÆØÅ") >= 0) danish++;
            else if (EnglishWords.Contains(word)) english++;
        }
        return danish == english ? null : danish > english;
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
