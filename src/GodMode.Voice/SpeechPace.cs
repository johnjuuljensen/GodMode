using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// How fast a line is said, by how long it is (#547): an ack, a read-back or "Sendt" at the synthesizer's speed, which is
/// the fastest it takes (<see cref="CloudVoiceProviders.Speed"/>), and a long reading (a reply, a status, a question with
/// its options) slower, easing from <see cref="FastUpToWords"/> words down to <see cref="SlowSpeed"/> at
/// <see cref="SlowFromWords"/>. The thresholds are to be tuned by ear. The speed is a factor on the session's
/// (<see cref="NodeResult.Speed"/>), so the user's "langsommere" and "hurtigere" still move it.
/// </summary>
public static class SpeechPace
{
    /// <summary>A line of up to this many words is said at <see cref="FastSpeed"/>.</summary>
    public const int FastUpToWords = 15;

    /// <summary>A line of this many words or more is said at <see cref="SlowSpeed"/>.</summary>
    public const int SlowFromWords = 40;

    /// <summary>The synthesizer's own speed, as ElevenLabs takes it: the speed of a short line.</summary>
    public const double FastSpeed = CloudVoiceProviders.Speed;

    /// <summary>The speed of a long reading, as ElevenLabs takes it (its natural speed).</summary>
    public const double SlowSpeed = 1.0;

    /// <summary>
    /// The factor on the session's speed <paramref name="text"/> is said at: null for a short line (the synthesizer's own
    /// speed), down to <see cref="SlowSpeed"/> / <see cref="FastSpeed"/> for a long one, in a straight line between.
    /// </summary>
    public static double? Of(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words <= FastUpToWords) return null;
        var slowed = Math.Min(1.0, (words - FastUpToWords) / (double)(SlowFromWords - FastUpToWords));
        return (FastSpeed + (SlowSpeed - FastSpeed) * slowed) / FastSpeed;
    }
}

/// <summary>
/// The graph, its replies said at their pace (<see cref="SpeechPace"/>, #547): a reply that sets its own speed keeps it.
/// It also keeps what was said for a part of a reply being read (<see cref="VoiceConversation.PartSaid"/>), so "fra
/// starten" and "gentag afsnittet" say it again word for word, with no model call (<see cref="ReplayNode"/>).
/// </summary>
public sealed class PacedNode(INode graph, VoiceConversation conversation) : INode
{
    public string Id => graph.Id;
    public int Priority => graph.Priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        NodeResult? result = null;
        try
        {
            result = await graph.EvaluateAsync(context, ct);
        }
        finally
        {
            // What this evaluation said, for the part a tool read in it; a part read and not said is not kept
            conversation.PartSaid(result?.ResponseText is { Length: > 0 } said ? said : null);
        }
        return result is { ResponseText: { Length: > 0 } text, Speed: null } ? result with { Speed = SpeechPace.Of(text) } : result;
    }
}
