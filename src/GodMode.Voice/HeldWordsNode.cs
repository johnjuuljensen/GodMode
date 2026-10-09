using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, which keeps what it waited past (#529): a final the model took for the start of what the user is
/// saying (its answer's <c>wait</c>, a pause the transcriber committed on mid-sentence) is joined to the next
/// final it is given, so the whole of it is acted on, and sent, not its last part alone. ChatNode answers nothing for
/// a final it waits past, and gives null; what it held is dropped after <see cref="VoiceConversation.HeldFor"/>.
/// </summary>
public sealed class HeldWordsNode(INode chat, VoiceConversation conversation) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { IsPartial: false } transcription
            || (context.CleanedText ?? transcription.Text) is not { } text || string.IsNullOrWhiteSpace(text))
            return await chat.EvaluateAsync(context, ct);

        var cleaned = context.CleanedText;
        var whole = conversation.TakeHeld() is { } held ? $"{held} {text.Trim()}" : text.Trim();
        if (whole.Length > text.Trim().Length)
        {
            context.Log?.Log("HELD", $"'{text}' goes on from what was held: \"{whole}\"");
            context.CleanedText = whole;
        }
        try
        {
            var result = await chat.EvaluateAsync(context, ct);
            if (result is null)
            {
                context.Log?.Log("HELD", $"Waited past \"{whole}\": held for the next final");
                conversation.Hold(whole);
            }
            return result;
        }
        finally
        {
            context.CleanedText = cleaned;
        }
    }
}
