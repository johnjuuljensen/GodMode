using System.Text.RegularExpressions;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// A final that is only a hesitation ("Øh, det…", "Um, so"), the user thinking aloud before they say what they want:
/// nothing is said, and the model is not asked (#526), which took a round of several seconds to decide to wait. It
/// takes a final of hesitation sounds and the small words said around them, with at least one sound: "det" alone is
/// no hesitation. Above the yes a create waits on, so an "øh" before the user's answer to a read-back drops nothing.
/// </summary>
public sealed partial class HesitationNode(string id, int priority) : INode
{
    /// <summary>The sounds a speaker makes while thinking, Danish first, with English.</summary>
    private static readonly HashSet<string> Sounds = new(StringComparer.OrdinalIgnoreCase)
        { "øh", "øhh", "øhm", "øhhm", "øhmm", "eh", "ehm", "ehh", "hm", "hmm", "hmmm", "mm", "mmm", "uh", "uhm", "um", "umm", "ah", "æh", "æhm" };

    /// <summary>The small words said around them while thinking: never a whole ask by themselves.</summary>
    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
        { "det", "den", "så", "og", "men", "altså", "jamen", "lige", "vent", "the", "so", "and", "but", "well", "wait", "like" };

    public string Id => id;
    public int Priority => priority;

    /// <summary>Whether the words are a hesitation alone: its sounds and small words, with at least one sound.</summary>
    public static bool Hesitates(string text)
    {
        var words = Word().Matches(text).Select(m => m.Value).ToList();
        return words.Any(Sounds.Contains) && words.All(w => Sounds.Contains(w) || Small.Contains(w));
    }

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { IsPartial: false } transcription
            || (context.CleanedText ?? transcription.Text) is not { } text || !Hesitates(text))
            return Task.FromResult<NodeResult?>(null);

        context.Log?.Log("HESITATION", $"'{text}': waiting for the rest");
        return Task.FromResult<NodeResult?>(new NodeResult());
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
