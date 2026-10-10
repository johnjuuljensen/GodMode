using System.Text.RegularExpressions;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The chat node, whose word on a send is the code's (issue #375): "Sendt" is said only for an answer
/// <see cref="VoiceTools.AnswerAsync"/> sent in this evaluation, in the code's words (<see cref="VoicePhrases.Sent"/>)
/// in place of the model's reply. A reply that claims a send when none went out in it (no tool call, or one that did
/// nothing) is not said: the user hears that nothing was sent (<see cref="VoicePhrases.NothingSent"/>). Unless it repeats
/// a project's own words a tool read out in this evaluation (#411): a reply read word for word that starts "Sendt til
/// review." is the project's, said as it is. A reply that says every send in the code's words already is kept whole
/// (#523): the code said it after the part of a compound ask before it ("hvad venter, og svar 283 ja",
/// <see cref="CodeSaysInference"/>), which is not to be lost.
/// </summary>
public sealed partial class SentNode(INode chat, VoiceConversation conversation, VoicePhrases phrases) : INode
{
    public string Id => chat.Id;
    public int Priority => chat.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        _ = conversation.TakeSent();   // a send in an earlier evaluation, or one that failed, is none of this one's
        _ = conversation.TakeReadOut();
        var result = await chat.EvaluateAsync(context, ct);
        var readOut = conversation.TakeReadOut();
        var sent = conversation.TakeSent();
        if (sent.Count > 0 && result?.ResponseText is { } codes && sent.All(name => codes.Contains(phrases.Sent([name]), StringComparison.Ordinal)))
            return result;
        var said = sent.Count > 0 ? phrases.Sent(sent)
            : result?.ResponseText is { } reply && ClaimsSend(reply) && !readOut.Any(text => Repeats(reply, text)) ? phrases.NothingSent
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

    /// <summary>How much of a reply's start <see cref="Repeats"/> compares: a sentence's worth, which a reading cut short later still has.</summary>
    private const int RepeatedStart = 40;

    /// <summary>
    /// Whether <paramref name="reply"/> starts as a sentence of <paramref name="readOut"/> does, a tool's text: its first
    /// <see cref="RepeatedStart"/> characters, whitespace collapsed and case ignored, are there, at the text's start or
    /// after the end of a sentence or a label ("Last reply: Sendt til review. …"). Not "sendt" in the middle of a
    /// sentence ("PR'en er sendt."): a bare "Sendt." does not repeat that.
    /// </summary>
    public static bool Repeats(string reply, string readOut)
    {
        var start = Collapsed(reply);
        start = start[..Math.Min(start.Length, RepeatedStart)];
        if (start.Length == 0)
            return false;

        var text = Collapsed(readOut);
        for (var at = text.IndexOf(start, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(start, at + 1, StringComparison.OrdinalIgnoreCase))
            if (text[..at].TrimEnd() is var before && (before.Length == 0 || before[^1] is '.' or ':' or '!' or '?' or '"'))
                return true;
        return false;
    }

    private static string Collapsed(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(?:(?:det|svaret|den|it|the answer)\s+(?:er|blev|is|was|has\s+been)\s+)?(?:sendt|sent)(?:\s+(?:til|to)\s+[^\s,.:;!?]+)?\s*(?:[.!,:;]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Claim();
}
