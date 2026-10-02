using System.Text.RegularExpressions;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose word on a send is the code's (issue #375): "Sendt" is said only for an answer
/// <see cref="VoiceTools.AnswerAsync"/> sent in this evaluation, in the code's words (<see cref="VoicePhrases.Sent"/>)
/// in place of the model's reply. A reply that claims a send when none went out in it (no tool call, or one that did
/// nothing) is not said: the user hears that nothing was sent (<see cref="VoicePhrases.NothingSent"/>).
/// </summary>
public sealed partial class SentNode(INode chat, VoiceConversation conversation, VoicePhrases phrases) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        _ = conversation.TakeSent();   // a send in an earlier evaluation, or one that failed, is none of this one's
        var result = await chat.EvaluateAsync(context, ct);
        var said = conversation.TakeSent() is { Count: > 0 } sent ? phrases.Sent(sent)
            : result?.ResponseText is { } reply && ClaimsSend(reply) ? phrases.NothingSent
            : null;
        if (said is null)
            return result;

        context.Log?.Log("SENT", $"Said \"{said}\" in place of \"{result?.ResponseText}\"");
        return ChatReply.Replace(context, result, said);
    }

    /// <summary>
    /// Whether a reply says an answer was sent, in the bot's own protocol form at its start: "Sendt.", "Sendt til 283.",
    /// "Det er sendt.", "Sent to 283, it continues." Not "Intet sendt", and not a project's own text read out ("283
    /// spørger: PR'en er sendt til review", "Sent the PR for review"): the sent word must be the reply's own statement,
    /// ended there, or followed only by whom it went to.
    /// </summary>
    public static bool ClaimsSend(string reply) => Claim().IsMatch(reply);

    [GeneratedRegex(@"^\s*(?:(?:det|svaret|den|it|the answer)\s+(?:er|blev|is|was|has\s+been)\s+)?(?:sendt|sent)(?:\s+(?:til|to)\s+[^\s,.:;!?]+)?\s*(?:[.!,:;]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Claim();
}
