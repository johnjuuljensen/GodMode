using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Speech;

namespace GodMode.Voice;

/// <summary>What the user asks <see cref="ReplayNode"/> to say again.</summary>
public enum Replay
{
    /// <summary>"Gentag": the last line again, from the sentence it was cut in, when it was cut.</summary>
    Repeat,

    /// <summary>"Spol tilbage": from a sentence before where it was cut (the last sentence, when it was not).</summary>
    BackUp,

    /// <summary>"Fra starten": the reply being read from its first part, else the last line whole.</summary>
    FromStart,

    /// <summary>"Gentag afsnittet": the part of the reply being read that was said last, else the last line whole.</summary>
    Paragraph,
}

/// <summary>
/// Says it again, by code, with no model call (#547): "gentag" / "hvad sagde du" the last line, from the sentence it was
/// cut in; "spol tilbage" a sentence before that; "fra starten" the reply being read from its first part, and "mere"
/// goes on from the second; "gentag afsnittet" the part of it said last. From VoiceBot's <see cref="NodeContext.LastSpeech"/>
/// (what was said, by sentence, and where it was cut, johnjuuljensen/VoiceBot#96), and the reply's parts as they were
/// said (<see cref="ReplyReading.Said"/>). On a final whose words are its phrases alone (<see cref="WholeUtterance"/>), as
/// help and Done decide: said during a reading, the final cuts it first, and the cut is where it starts again.
/// <para>
/// A bare "tilbage" is <c>go_back</c>'s (#287), the focus before: rewinding is "spol tilbage" or "lidt tilbage", never
/// "tilbage" alone.
/// </para>
/// <para>
/// It also takes VoiceBot's playback words (<paramref name="playback"/>) when nothing is being said: "langsommere" /
/// "hurtigere" change the speed of what is said from then on, and "fortsæt" goes on with a line that was cut, from its
/// cut sentence. With nothing cut, "fortsæt" is the model's, and so is "pause" or "vent" with nothing to pause: a bare
/// "vent" answers a read-back as before. While the bot speaks, VoiceBot takes them itself, without a barge-in.
/// </para>
/// </summary>
public sealed class ReplayNode(string id, int priority, VoiceConversation conversation, VoicePhrases phrases, PlaybackCommands playback) : INode
{
    /// <summary>How many sentences "spol tilbage" goes back before the one that was cut.</summary>
    public const int BackUpSentences = 1;

    /// <summary>What asks for each, Danish first, with English. The whole utterance is one (or several) of them.</summary>
    public static readonly IReadOnlyList<(string Phrase, Replay Replay)> Phrases =
    [
        ("gentag", Replay.Repeat), ("gentag det", Replay.Repeat), ("gentag lige", Replay.Repeat), ("sig det igen", Replay.Repeat),
        ("hvad sagde du", Replay.Repeat),
        ("repeat", Replay.Repeat), ("repeat that", Replay.Repeat), ("say that again", Replay.Repeat), ("say again", Replay.Repeat),
        ("what did you say", Replay.Repeat), ("come again", Replay.Repeat),
        ("spol tilbage", Replay.BackUp), ("spol lidt tilbage", Replay.BackUp), ("lidt tilbage", Replay.BackUp), ("gå lidt tilbage", Replay.BackUp),
        ("back up", Replay.BackUp), ("rewind", Replay.BackUp), ("go back a bit", Replay.BackUp), ("back a bit", Replay.BackUp),
        ("fra starten", Replay.FromStart), ("forfra", Replay.FromStart), ("gentag fra starten", Replay.FromStart), ("læs forfra", Replay.FromStart),
        ("start forfra", Replay.FromStart), ("fra begyndelsen", Replay.FromStart),
        ("from the start", Replay.FromStart), ("from the beginning", Replay.FromStart), ("repeat from the start", Replay.FromStart),
        ("start over", Replay.FromStart), ("from the top", Replay.FromStart),
        ("gentag afsnittet", Replay.Paragraph), ("afsnittet igen", Replay.Paragraph), ("læs afsnittet igen", Replay.Paragraph),
        ("repeat the paragraph", Replay.Paragraph), ("repeat that paragraph", Replay.Paragraph), ("that paragraph again", Replay.Paragraph),
    ];

    /// <summary>
    /// What help says of these (<see cref="HelpNode.Hints"/>): the replays, and steering a reading. Not tools, so help is
    /// given them as commands the graph has (<see cref="HelpNode"/>'s <c>commands</c>).
    /// </summary>
    public const string ReplayCommand = "replay";

    /// <inheritdoc cref="ReplayCommand"/>
    public const string PlaybackCommand = "playback";

    /// <summary>The commands of this node help says.</summary>
    public static readonly IReadOnlyList<string> Commands = [ReplayCommand, PlaybackCommand];

    private static readonly IReadOnlyList<string[]> PhraseTokens = [.. Phrases.Select(p => CommandResolver.Tokenize(p.Phrase))];

    public string Id => id;
    public int Priority => priority;

    /// <summary>What the words ask for, if they are this node's phrases alone; the first phrase's, when several.</summary>
    public static Replay? Asked(string text) =>
        WholeUtterance.Cover(CommandResolver.Tokenize(text), PhraseTokens, open: false) is { } cover ? Phrases[cover.First].Replay : null;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { IsPartial: false } transcription)
            return Task.FromResult<NodeResult?>(null);

        var text = context.CleanedText ?? transcription.Text;
        var said = Asked(text) is { } replay ? Again(context, replay)
            : playback.TryMatch(text, out var control) ? Steer(context, control)
            : null;
        if (said is null)
            return Task.FromResult<NodeResult?>(null);

        context.Log?.Log("REPLAY", $"'{text}' → \"{said}\"");
        context.AddUserMessage(text);
        context.AddAssistantMessage(said);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = said });
    }

    /// <summary>What to say for <paramref name="replay"/>.</summary>
    private string Again(NodeContext context, Replay replay)
    {
        // The reply being read, by its parts as they were said: from the first, or the one said last
        if (replay is Replay.FromStart or Replay.Paragraph && conversation.Reading is ReplyReading reading)
        {
            var index = replay == Replay.FromStart ? 0 : reading.Next - 1;
            if (reading.Said.TryGetValue(index, out var part))
            {
                conversation.Current = reading.Project;
                conversation.Reading = reading with { Next = index + 1 };
                return part;
            }
        }

        if (context.LastSpeech is not { } last)
            return phrases.NothingToRepeat;
        var from = replay switch
        {
            Replay.Repeat => Cut(last) ? last.FirstUnheardSentence : 0,
            Replay.BackUp => last.FirstUnheardSentence - BackUpSentences,
            _ => 0,
        };
        return last.From(from) is { Length: > 0 } rest ? rest : last.Text;
    }

    /// <summary>A playback word said while nothing is being said (VoiceBot takes it while the bot speaks); null for the model's.</summary>
    private string? Steer(NodeContext context, PlaybackControl control)
    {
        switch (control)
        {
            case PlaybackControl.Slower or PlaybackControl.Faster:
                var before = context.Speech.Speed;
                context.Speech.Speed = control == PlaybackControl.Slower ? before / playback.SpeedStep : before * playback.SpeedStep;
                return phrases.SpeedChanged(control == PlaybackControl.Slower, context.Speech.Speed != before);
            case PlaybackControl.Resume when context.LastSpeech is { } last && Cut(last) && last.From(last.FirstUnheardSentence) is { Length: > 0 } rest:
                return rest;
            default:
                return null;
        }
    }

    /// <summary>Whether the line was cut before its end: a barge-in, a pause given up, another line in its place.</summary>
    private static bool Cut(SpokenUtterance last) =>
        last.State is SpokenUtteranceState.Interrupted or SpokenUtteranceState.Paused && last.Heard is not null;
}
