using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// "Hjælp" / "help": says what the user can ask, from the tools the graph has. It answers on the first partial that
/// is its phrases alone (one, or several: "Hjælp. Hjælp."), as VoiceBot's CommandNode answers a command, without waiting for the final, and claims the
/// help's words when they come again in that utterance (later partials, and the final that ends it, the same words
/// or more of help's), so the chat node never answers them. New words after them are the user's next sentence (VoiceBot#62 can hold an utterance open):
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
        (VoiceTools.StartSession, "start issue og et nummer", "start issue and a number"),
        (AnnouncementTools.Mute.Name, "stille", "quiet"),
        (AnnouncementTools.Unmute.Name, "sig til igen", "you can talk again"),
    ];

    private static readonly IReadOnlyList<(string[] Tokens, bool Danish)> PhraseTokens =
        [.. Phrases.Select(p => (CommandResolver.Tokenize(p.Phrase), p.Danish))];

    public string Id => id;
    public int Priority => priority;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { } transcription)
            return Task.FromResult<NodeResult?>(null);

        var text = context.CleanedText ?? transcription.Text;
        var asked = Asked(CommandResolver.Tokenize(text));

        // Help answered in this utterance, until the final: help's words again (the same, or more of them) are the same
        // request, and other words are not
        if (context.GraphState.Get(context.StateKey, false))
        {
            if (asked is null || !transcription.IsPartial)
                context.GraphState.Remove(context.StateKey);
            if (asked is not null)
                return Task.FromResult<NodeResult?>(new NodeResult());
        }

        if (asked is not { } danish)
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
    /// Whether the words are help's phrases alone, one after another, and if so whether the first is Danish; null when
    /// any word is not part of a phrase.
    /// </summary>
    public static bool? Asked(string[] tokens)
    {
        // From the end: the language of the first of the phrases that make up the words from each position on, null
        // where no phrases do
        var from = new bool?[tokens.Length + 1];
        for (var at = tokens.Length - 1; at >= 0; at--)
        {
            foreach (var (phrase, danish) in PhraseTokens)
            {
                var end = at + phrase.Length;
                if (end <= tokens.Length && (end == tokens.Length || from[end] is not null)
                    && tokens.AsSpan(at, phrase.Length).SequenceEqual(phrase))
                {
                    from[at] = danish;
                    break;
                }
            }
        }
        return tokens.Length == 0 ? null : from[0];
    }

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
