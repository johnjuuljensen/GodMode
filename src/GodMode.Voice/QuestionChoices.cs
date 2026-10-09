using System.Text.RegularExpressions;
using GodMode.Shared.Models;

namespace GodMode.Voice;

/// <summary>
/// A pending AskUserQuestion's options by voice (#529): what the tools tell the model of them, and the option an answer
/// picks, by its label or its number ("den anden", "option 2"), which is sent as the inbox sends it
/// (<see cref="IGodModeServers.AnswerQuestionAsync"/>), not as text.
/// </summary>
public static partial class QuestionChoices
{
    /// <summary>The ordinals an option is picked by, Danish first: "den første", "the second".</summary>
    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["første"] = 1, ["anden"] = 2, ["andet"] = 2, ["tredje"] = 3, ["fjerde"] = 4, ["femte"] = 5, ["sidste"] = -1,
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["last"] = -1,
    };

    /// <summary>The question voice can answer by an option: the one question of the request, with options. Null otherwise.</summary>
    public static QuestionItem? Single(PendingQuestion? pending) =>
        pending is { Questions: [{ Options.Count: > 0 } only] } ? only : null;

    /// <summary>
    /// The options, as a tool's text tells the model of them: "Options: 1. "Yes, file them"; 2. "Not now"." for each
    /// question that has any, with the question for each of several. Empty when there are none.
    /// </summary>
    public static string Described(PendingQuestion? pending)
    {
        if (pending is not { Questions: [_, ..] questions } || questions.All(q => q.Options.Count == 0))
            return "";
        string Options(QuestionItem q) =>
            string.Join("; ", q.Options.Select((o, i) => $"{i + 1}. \"{o.Label}\"")) + (q.MultiSelect ? " (several may be picked)" : "");
        return questions is [var one]
            ? $" Options: {Options(one)}. An answer that picks one gives it to {VoiceTools.Answer} as {VoiceTools.OptionParameter}."
            : " " + string.Join(" ", questions.Where(q => q.Options.Count > 0).Select(q => $"Options for \"{q.Question}\": {Options(q)}.")) +
              " Several questions are answered on screen, or in the user's words as text.";
    }

    /// <summary>
    /// The labels <paramref name="said"/> picks of the question's options: one by its label (case and punctuation
    /// ignored), by the start of one that only it starts so ("ja" of "Ja, opret dem"), or by its number or ordinal
    /// ("2", "to", "valg 2", "den anden", "the last"); several, for a multi-select, said with commas or "og"/"and".
    /// Null when it picks none of them, or any part of it picks none.
    /// </summary>
    public static IReadOnlyList<string>? Pick(QuestionItem question, string said)
    {
        if (Key(said) is not { Length: > 0 } whole)
            return null;
        if (One(question, whole) is { } label)
            return [label];
        if (!question.MultiSelect)
            return null;
        var parts = Separator().Split(said).Select(Key).Where(p => p.Length > 0).ToList();
        if (parts.Count < 2)
            return null;
        var labels = parts.Select(p => One(question, p)).ToList();
        return labels.Any(l => l is null) ? null : [.. labels.OfType<string>().Distinct()];
    }

    /// <summary>The answer to send for the labels picked: a multi-select's joined with ", ", as the hub takes them.</summary>
    public static string Joined(IReadOnlyList<string> labels) => string.Join(", ", labels);

    private static string? One(QuestionItem question, string key)
    {
        var options = question.Options;
        if (options.FirstOrDefault(o => Key(o.Label) == key) is { } exact)
            return exact.Label;
        var number = Lead().Replace(key, "").Trim();
        if ((Ordinals.TryGetValue(number, out var ordinal) ? ordinal : DanishNumbers.Parse(number)) is { } n
            && (n == -1 ? options.Count : n) is var index and >= 1 && index <= options.Count)
            return options[index - 1].Label;
        var started = options.Where(o => Key(o.Label).StartsWith(key + " ", StringComparison.Ordinal)).ToList();
        return started is [var only] ? only.Label : null;
    }

    /// <summary>A text as options are compared: its words, in lower case, one space between them.</summary>
    private static string Key(string text) => string.Join(' ', Word().Matches(text.ToLowerInvariant()).Select(m => m.Value));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\s*(?:,|\bog\b|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    /// <summary>What is said before an option's number or ordinal: "valg 2", "option 2", "den anden", "the second one".</summary>
    [GeneratedRegex(@"^(?:(?:den|det|the|nummer|number|nr|valg|valgmulighed|mulighed|option|svar)\s+)+|\s+(?:one|mulighed|valg)$")]
    private static partial Regex Lead();
}
