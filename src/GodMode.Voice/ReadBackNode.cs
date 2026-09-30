using Microsoft.Extensions.AI;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose reply is replaced by a create's read-back when its tools settled on one
/// (<see cref="SessionCreates.TakeProposed"/>): the question a yes answers is the code's fixed words
/// (<see cref="VoicePhrases.ReadBack"/>), never the model's, and the create is armed when they start playing.
/// </summary>
public sealed class ReadBackNode(INode chat, SessionCreates creates, VoicePhrases phrases) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        _ = creates.TakeProposed();   // one settled in an evaluation that failed is never read back later
        var result = await chat.EvaluateAsync(context, ct);
        if (creates.TakeProposed() is not { } request)
            return result;

        var readBack = phrases.ReadBack(request);
        creates.ReadingBack(request, readBack);
        context.Log?.Log("CREATE", $"Read back \"{readBack}\" for {request.What}, in place of \"{result?.ResponseText}\"");

        // The model's reply, if it gave one, is not said: the history holds what was
        if (result?.ResponseText is { } reply && context.History is [.., { Role: var role } last] && role == ChatRole.Assistant && last.Text == reply)
            context.History[^1] = new ChatMessage(ChatRole.Assistant, readBack);
        else
        {
            if ((context.CleanedText ?? context.LatestTranscription?.Text) is { } text) context.AddUserMessage(text);
            context.AddAssistantMessage(readBack);
        }
        return (result ?? new NodeResult()) with { ResponseText = readBack };
    }
}
