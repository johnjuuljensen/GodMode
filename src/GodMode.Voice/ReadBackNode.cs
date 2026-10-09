using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose reply is replaced by a create's read-back when its tools settled on one
/// (<see cref="SessionCreates.TakeProposed"/>), or a delete's (<see cref="SessionDeletes.TakeProposed"/>, #532): the
/// question a yes answers is the code's fixed words (<see cref="VoicePhrases.ReadBack"/>,
/// <see cref="VoicePhrases.DeleteReadBack"/>), never the model's, and it is armed when they start playing. An answer
/// to a read-back with a change in it (<see cref="SessionCreates.Correct"/>, #449) reaches the chat with the create it
/// changes, so the model can propose it again as changed. The user's words are given to the creates first
/// (<see cref="SessionCreates.Heard"/>), so a draft changes only on what they said (#473).
/// </summary>
public sealed class ReadBackNode(INode chat, SessionCreates creates, SessionDeletes deletes, VoicePhrases phrases) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        try
        {
            return await ReadBackAsync(context, ct);
        }
        finally
        {
            // What no longer waits on the user (a question answered, a create dropped) lets the held announcements go
            creates.Settled();
            deletes.Settled();
        }
    }

    private async Task<NodeResult?> ReadBackAsync(NodeContext context, CancellationToken ct)
    {
        _ = creates.TakeProposed();   // one settled in an evaluation that failed is never read back later
        _ = deletes.TakeProposed();
        // The user's own words, before the chat hears them: only these change a draft's root, action or issue (#473)
        if (context.LatestTranscription is { IsPartial: false } final)
            creates.Heard(context.CleanedText ?? final.Text);
        NodeResult? result;
        if (creates.TakeCorrected() is { } corrected && (context.CleanedText ?? context.LatestTranscription?.Text) is { } said)
        {
            var cleaned = context.CleanedText;
            context.CleanedText = $"{said}\n{corrected.Note}";
            try { result = await chat.EvaluateAsync(context, ct); }
            finally { context.CleanedText = cleaned; }
        }
        else
            result = await chat.EvaluateAsync(context, ct);
        if (deletes.TakeProposed() is { } delete)
        {
            // A create settled on in the same turn is not read back: one question at a time
            _ = creates.TakeProposed();
            var asked = phrases.DeleteReadBack(delete);
            deletes.ReadingBack(delete, asked);
            context.Log?.Log("DELETE", $"Read back \"{asked}\" for {delete.What}, in place of \"{result?.ResponseText}\"");
            return ChatReply.Replace(context, result, asked);
        }
        if (creates.TakeProposed() is not { } request)
            return result;

        var readBack = phrases.ReadBack(request);
        creates.ReadingBack(request, readBack);
        context.Log?.Log("CREATE", $"Read back \"{readBack}\" for {request.What}, in place of \"{result?.ResponseText}\"");
        return ChatReply.Replace(context, result, readBack);
    }
}
