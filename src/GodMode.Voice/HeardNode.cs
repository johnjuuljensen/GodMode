using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, told whether the final it answers was heard more than one way: a final that carries earlier readings
/// (VoiceBot#61) goes to the model with them, and the model is told to answer what the user most plausibly meant. What
/// voice does on the user's behalf must not rest on that guess: while the chat answers such a final,
/// <see cref="VoiceConversation.Unsure"/> is set, so the tools that act (answer, mark seen, mute) do nothing and say to
/// ask, and the next final, heard one way, acts. A reading counts only when the final contradicts it: one the final
/// just goes on from is no other way of hearing it (VoiceBot's <c>TranscriptionReadings</c>).
/// </summary>
public sealed class HeardNode(INode chat, VoiceConversation conversation) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        conversation.Unsure = context.LatestTranscription is { IsPartial: false, Readings: [_, ..] readings } transcription
            ? new HeardTwoWays(context.CleanedText ?? transcription.Text, readings)
            : null;
        try
        {
            return await chat.EvaluateAsync(context, ct);
        }
        finally
        {
            conversation.Unsure = null;
        }
    }
}
