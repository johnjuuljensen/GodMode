using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// "Færdig" / "done": the user is done talking, and the mic closes (<see cref="VoiceMic.Done"/>), with its falling tone
/// as the only answer. On a final whose words are its phrases alone (<see cref="WholeUtterance"/>, as
/// <see cref="HelpNode"/> decides), never on a partial: "færdig" may begin "færdig med 283?". Its words are among the
/// keyterms (<see cref="GodModeGraph.CommandWords"/>).
/// </summary>
public sealed class DoneNode(string id, int priority, Action done) : INode
{
    /// <summary>What says the user is done, Danish first, with English.</summary>
    public static readonly IReadOnlyList<string> Phrases = ["færdig", "færdig tak", "det var alt", "done", "that's all"];

    private static readonly IReadOnlyList<string[]> PhraseTokens = [.. Phrases.Select(CommandResolver.Tokenize)];

    public string Id => id;
    public int Priority => priority;

    /// <summary>Whether the words are Done's phrases alone.</summary>
    public static bool Said(string text) => WholeUtterance.Cover(CommandResolver.Tokenize(text), PhraseTokens, open: false) is not null;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { IsPartial: false } transcription)
            return Task.FromResult<NodeResult?>(null);

        var text = context.CleanedText ?? transcription.Text;
        if (!Said(text))
            return Task.FromResult<NodeResult?>(null);

        context.Log?.Log("DONE", $"'{text}': the mic closes");
        done();
        return Task.FromResult<NodeResult?>(new NodeResult());
    }
}
