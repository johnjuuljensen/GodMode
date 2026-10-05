using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose reply is replaced by the sessions' own spoken replies a tool read out in this evaluation
/// (issue #384): a session that gave one (<c>speak</c>) is heard in its own words, word for word, after a lead-in that
/// names it (<see cref="VoicePhrases.Spoken"/>), not in the model's summary of a reply it wrote for a screen. One
/// project's turn with none is the model's to say, as before.
/// </summary>
public sealed class SpokenNode(INode chat, VoiceConversation conversation, VoicePhrases phrases) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        _ = conversation.TakeSpoken();   // one read in an earlier evaluation, or one that failed, is none of this one's
        var result = await chat.EvaluateAsync(context, ct);
        if (conversation.TakeSpoken() is not { Count: > 0 } spoken)
            return result;
        // The code said them already, word for word, in its own words for the turn (#456), maybe after others (#507)
        if (result?.ResponseText is { } reply && spoken.All(s => reply.Contains(s.Item.Spoken!, StringComparison.Ordinal)))
            return result;

        var said = string.Join(" ", spoken.Select(s => phrases.Spoken(s.Name, s.Item)).OfType<string>().Select(GodModeAnnouncementFormatter.Sentence));
        if (said.Length == 0)
            return result;
        context.Log?.Log("SPOKEN", $"Said \"{said}\" in place of \"{result?.ResponseText}\"");
        return ChatReply.Replace(context, result, said);
    }
}
