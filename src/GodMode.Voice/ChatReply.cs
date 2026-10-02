using Microsoft.Extensions.AI;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>The chat node's reply, replaced by the code's own words (<see cref="ReadBackNode"/>, <see cref="SentNode"/>).</summary>
internal static class ChatReply
{
    /// <summary>
    /// <paramref name="said"/> in place of what the chat replied: the model's reply, if it gave one, is not said, so
    /// the history holds what was.
    /// </summary>
    public static NodeResult Replace(NodeContext context, NodeResult? result, string said)
    {
        if (result?.ResponseText is { } reply && context.History is [.., { Role: var role } last] && role == ChatRole.Assistant && last.Text == reply)
            context.History[^1] = new ChatMessage(ChatRole.Assistant, said);
        else
        {
            if ((context.CleanedText ?? context.LatestTranscription?.Text) is { } text) context.AddUserMessage(text);
            context.AddAssistantMessage(said);
        }
        return (result ?? new NodeResult()) with { ResponseText = said };
    }
}
