using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// "Hjælp" / "help": says what the user can ask, from the tools the graph has, when the utterance is its phrases alone
/// (one, or several: "Hjælp. Hjælp."). Phrases of one word it answers on the first partial, as VoiceBot's CommandNode
/// answers a command, without waiting for the final; a phrase of several words opens ordinary commands too ("Hvad kan
/// jeg" of "Hvad kan jeg svare 283?"), so one of those is help on the final only. Once it has answered, it claims what
/// comes again in that utterance while it is, or may still grow into, help's words (later partials, and the final that
/// ends it), so the chat node never answers them, nor help twice. New words after them are the user's next sentence (VoiceBot#62 can hold an utterance open):
/// they release the claim and go on, whole, to the chat node.
/// Help decides on the final's own words: the earlier readings a final carries (VoiceBot#61) are the model's, never help's.
/// Not a CommandNode, even with VoiceBot#65's phrase keywords and claim: a keyword matches anywhere in the utterance, so
/// "hvad kan du fortælle om 283?" would be help, and the claim takes whatever starts with the words it fired on, so
/// "Hjælp. Kan du høre mig?" would never reach the chat node (johnjuuljensen/VoiceBot#71).
/// </summary>
public sealed class HelpNode(string id, int priority) : INode
{
    /// <summary>
    /// What asks for help: the whole utterance, as recognized, is one or more of these, and nothing else ("Hjælp.
    /// Hjælp.", "Hjælp, hvad kan jeg sige?"). Danish first, with English; help answers in the first phrase's language.
    /// </summary>
    public static readonly IReadOnlyList<(string Phrase, bool Danish)> Phrases =
    [
        ("hjælp", true), ("kommandoer", true), ("hvad kan du", true), ("hvad kan jeg sige", true),
        ("hvad kan jeg gøre", true), ("hvad kan jeg", true),
        ("help", false), ("commands", false), ("what can you do", false), ("what can i say", false),
        ("what can i do", false), ("what can i", false),
    ];

    /// <summary>What the user says to use each tool, in Danish and English, in the order help says them.</summary>
    public static readonly IReadOnlyList<(string Tool, string Danish, string English)> Hints =
    [
        (VoiceTools.WhatNeedsMe, "hvad venter", "what needs me"),
        (VoiceTools.ListProjects, "hvilke projekter er der", "which projects"),
        (VoiceTools.ProjectStatus, "status og et projekt", "status and a project"),
        (VoiceTools.ReadReply, "læs svaret og et projekt", "read the reply and a project"),
        (VoiceTools.ReadMore, "læs videre", "read on"),
        (VoiceTools.Answer, "svar at og dit svar", "answer that and your answer"),
        (VoiceTools.MarkSeen, "læst", "seen"),
        (VoiceTools.SetImportance, "marker som vigtig", "mark as important"),
        (VoiceTools.StartSession, "start issue og et nummer", "start issue and a number"),
        (AnnouncementTools.Mute.Name, "stille", "quiet"),
        (AnnouncementTools.Unmute.Name, "sig til igen", "you can talk again"),
    ];

    private static readonly IReadOnlyList<string[]> PhraseTokens = [.. Phrases.Select(p => CommandResolver.Tokenize(p.Phrase))];

    public string Id => id;
    public int Priority => priority;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { } transcription)
            return Task.FromResult<NodeResult?>(null);

        var text = context.CleanedText ?? transcription.Text;
        var tokens = CommandResolver.Tokenize(text);
        var asked = Asked(tokens);

        // Help answered in this utterance, until the final: help's words again (the same, more of them, or the start of
        // more) are the same request, and other words are not
        if (context.GraphState.Get(context.StateKey, false))
        {
            var same = asked is not null || (transcription.IsPartial && MayBecomeHelp(tokens));
            if (!same || !transcription.IsPartial)
                context.GraphState.Remove(context.StateKey);
            if (same)
                return Task.FromResult<NodeResult?>(new NodeResult());
        }

        if (asked is not var (danish, onPartial) || (transcription.IsPartial && !onPartial))
            return Task.FromResult<NodeResult?>(null);

        if (transcription.IsPartial)
            context.GraphState.Set(context.StateKey, true);

        var help = Say(context.Tools?.ResolveAll().Keys ?? [], danish);
        context.Log?.Log("HELP", $"'{text}' → \"{help}\"");
        context.AddUserMessage(text);
        context.AddAssistantMessage(help);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = help });
    }

    /// <summary>
    /// Whether the words are help's phrases alone, one after another: if so, whether the first is Danish, and whether
    /// each is of one word (help on a partial); null when any word is not part of a phrase.
    /// </summary>
    public static (bool Danish, bool OnPartial)? Asked(string[] tokens) => Cover(tokens, open: false);

    /// <summary>Whether the words are help's phrases, the last of them perhaps only begun ("Hjælp. Hvad kan").</summary>
    public static bool MayBecomeHelp(string[] tokens) => Cover(tokens, open: true) is not null;

    /// <summary>The words as help's phrases, the last of them perhaps only begun when <paramref name="open"/>.</summary>
    private static (bool Danish, bool OnPartial)? Cover(string[] tokens, bool open) =>
        WholeUtterance.Cover(tokens, PhraseTokens, open) is { } cover ? (Phrases[cover.First].Danish, cover.OneWordEach) : null;

    /// <summary>What help says for a graph with these tools: the hint of each it has, in one short sentence.</summary>
    public static string Say(IEnumerable<string> tools, bool danish)
    {
        var present = tools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hints = Hints.Where(h => present.Contains(h.Tool)).Select(h => danish ? h.Danish : h.English).ToList();
        return (hints, danish) switch
        {
            ([], true) => "Jeg kan ingenting lige nu.",
            ([], false) => "I can't do anything right now.",
            ([var one], true) => $"Du kan sige: {one}.",
            ([var one], false) => $"You can say: {one}.",
            (_, true) => $"Du kan sige: {string.Join(", ", hints[..^1])} eller {hints[^1]}.",
            (_, false) => $"You can say: {string.Join(", ", hints[..^1])} or {hints[^1]}.",
        };
    }
}
