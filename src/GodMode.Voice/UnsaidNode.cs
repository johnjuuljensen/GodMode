using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose turn says the code's words it held (<see cref="CodeSaysInference"/>, #507) however it ends (#523):
/// a turn the model never ended, calling tool after tool until VoiceBot's round limit, gives no reply, and the words for
/// the parts of a compound ask done before it would be lost with it. They are said in its place.
/// </summary>
public sealed class UnsaidNode(INode chat, CodeSaysInference inference) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        var result = await chat.EvaluateAsync(context, ct);
        if (result?.ResponseText is { Length: > 0 } || inference.TakeHeld() is not { } held)
            return result;

        context.Log?.Log("UNSAID", $"The turn ended with no reply: said \"{held}\", which was held for it");
        return ChatReply.Replace(context, result, held);
    }
}
