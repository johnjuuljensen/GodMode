using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// "Hjælp" / "help": says what the user can ask, from the tools the graph has. It answers on the first partial that
/// is one of its phrases, as VoiceBot's CommandNode answers a command, without waiting for the final, and claims the
/// rest of that utterance (the partials that grow from it, and the final that ends it), so the chat node never
/// answers the same words again.
/// Not a CommandNode: on VoiceBot's pin its keywords are single words, so "hvad kan du" never matches, and it claims
/// only an exact repeat, so "Hjælp. Kører" would say the list again (johnjuuljensen/VoiceBot#65).
/// </summary>
public sealed class HelpNode(string id, int priority) : INode
{
    /// <summary>
    /// What asks for help: the whole utterance, as recognized, is one of these. Danish first, with English; help
    /// answers in the phrase's language.
    /// </summary>
    public static readonly IReadOnlyList<(string Phrase, bool Danish)> Phrases =
    [
        ("hjælp", true), ("kommandoer", true), ("hvad kan du", true), ("hvad kan jeg sige", true),
        ("help", false), ("commands", false), ("what can you do", false), ("what can i say", false),
    ];

    /// <summary>What the user says to use each tool, in Danish and English, in the order help says them.</summary>
    public static readonly IReadOnlyList<(string Tool, string Danish, string English)> Hints =
    [
        (VoiceTools.WhatNeedsMe, "hvad venter", "what needs me"),
        (VoiceTools.ListProjects, "hvilke projekter er der", "which projects"),
        (VoiceTools.ProjectStatus, "status og et projekt", "status and a project"),
        (VoiceTools.Answer, "svar at og dit svar", "answer that and your answer"),
        (VoiceTools.MarkSeen, "læst", "seen"),
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
        var tokens = CommandResolver.Tokenize(text);

        // The utterance help answered, until its final: what grows from it is the same request
        if (context.GraphState.Get<string[]?>(context.StateKey, null) is { } answered)
        {
            var same = tokens.AsSpan().StartsWith(answered);
            if (!same || !transcription.IsPartial)
                context.GraphState.Remove(context.StateKey);
            if (same)
                return Task.FromResult<NodeResult?>(new NodeResult());
        }

        if (PhraseTokens.FirstOrDefault(p => tokens.AsSpan().SequenceEqual(p.Tokens)) is not { Tokens: not null } asked)
            return Task.FromResult<NodeResult?>(null);

        if (transcription.IsPartial)
            context.GraphState.Set(context.StateKey, tokens);

        var help = Say(context.Tools?.ResolveAll().Keys ?? [], asked.Danish);
        context.Log?.Log("HELP", $"'{text}' → \"{help}\"");
        context.AddUserMessage(text);
        context.AddAssistantMessage(help);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = help });
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
