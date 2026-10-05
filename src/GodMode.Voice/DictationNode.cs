using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// Dictation in the graph (#459, <see cref="Dictation"/>), above every other node: a final that starts one ("Diktér til
/// 283") is the code's, never the model's. While one is taken it claims everything the user says: each final is a part,
/// said nothing to, and partials are no one's (help never answers one), so no command is acted on but its terminators.
/// A terminator's outcome, and a dictation dropped on a tick, are said in the code's words. Built in GodMode, over
/// VoiceBot's finals as they are: a pause the transcriber commits on (johnjuuljensen/VoiceBot#73) ends a part, not the
/// dictation.
/// </summary>
public sealed class DictationNode(string id, int priority, Dictation dictation) : INode
{
    public string Id => id;
    public int Priority => priority;

    public async Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { } transcription)
            return Say(context, null, dictation.Tick());

        var active = dictation.Active;
        if (transcription.IsPartial)
            return active ? new NodeResult() : null;

        // The final's own words, as recognized: never the earlier readings, and never the model's
        var text = context.CleanedText ?? transcription.Text;
        if (active)
        {
            var said = await dictation.HearAsync(text, ct);
            context.Log?.Log("DICTATION", said is null ? $"Part: \"{text}\"" : $"'{text}' → \"{said}\"");
            return said is null ? new NodeResult() : Say(context, text, said);
        }

        if (await dictation.StartAsync(text, ct) is not { } answer)
            return null;
        context.Log?.Log("DICTATION", $"'{text}' → \"{answer}\"");
        return Say(context, text, answer);
    }

    /// <summary>What the code says, in the history as said; nothing when <paramref name="said"/> is null.</summary>
    private static NodeResult? Say(NodeContext context, string? heard, string? said)
    {
        if (said is null)
            return null;
        if (heard is not null) context.AddUserMessage(heard);
        context.AddAssistantMessage(said);
        return new NodeResult { ResponseText = said };
    }
}
