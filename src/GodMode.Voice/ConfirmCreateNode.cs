using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The yes a create waits on (<see cref="SessionCreates.Pending"/>): the final that follows the read-back is the answer.
/// Only a clear yes creates; anything else, a no or another sentence, cancels it, and is said to have. The model never
/// creates: the graph has no tool that does, so a yes it imagines, or a create on no answer, cannot happen. Partials
/// pass (a "ja" may go on as "ja, men i kappe"), and so does anything while no create waits.
/// </summary>
public sealed class ConfirmCreateNode(string id, int priority, SessionCreates creates, VoicePhrases phrases) : INode
{
    /// <summary>A clear yes: the whole utterance, as recognized, is one of these.</summary>
    public static readonly IReadOnlyList<string> Yes =
    [
        "ja", "jo", "jep", "ja tak", "ja gerne", "ja gør det", "gør det", "opret", "opret den", "ja opret den", "start den", "ja start den",
        "yes", "yeah", "yep", "yes please", "do it", "go ahead", "create it", "start it", "ok", "okay",
    ];

    private static readonly IReadOnlyList<string[]> YesTokens = [.. Yes.Select(CommandResolver.Tokenize)];

    public string Id => id;
    public int Priority => priority;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { IsPartial: false } transcription || creates.Pending is null)
            return Task.FromResult<NodeResult?>(null);

        var text = context.CleanedText ?? transcription.Text;
        var tokens = CommandResolver.Tokenize(text);
        var yes = YesTokens.Any(y => tokens.AsSpan().SequenceEqual(y));
        var request = yes ? creates.Confirm() : creates.Cancel();
        if (request is null)
            return Task.FromResult<NodeResult?>(null);

        var said = yes ? phrases.Creating : phrases.CreateCancelled;
        context.Log?.Log("CREATE", $"'{text}' → {(yes ? "create" : "cancel")} {request.What}");
        context.AddUserMessage(text);
        context.AddAssistantMessage(said);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = said });
    }
}
