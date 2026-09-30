using VoiceBot.Core.AI;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Graph.Nodes;
using VoiceBot.Core.Resources;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// GodMode's voice graph, after VoiceBot's VoiceControlGraph: a terse control loop in Danish protocol words, over
/// the hub (<see cref="VoiceTools"/>) instead of its fake system. A greeting, then one chat node with the tools, and
/// help (<see cref="HelpNode"/>) above it, which says what they are on the first partial that asks.
/// </summary>
public static class GodModeGraph
{
    public const string Id = "godmode-voice";

    /// <summary>What the user says to the bot, besides project handles; ElevenLabs is biased towards them.</summary>
    public static readonly IReadOnlyList<string> CommandWords =
        ["hvad venter", "projekter", "status","svar", "læst", "stille", "sig til igen", "hjælp", "GodMode", "pull request", "review"];

    /// <summary>The graph's tools: the hub's, and muting announcements.</summary>
    public static ToolSet AddTools(ToolSet set, VoiceTools tools) => tools.AddTo(set).AddAnnouncementTools();

    public static CompositeNode Build(IInferenceProvider inference, SessionLanguages languages, VoiceTools tools, VoicePhrases phrases)
    {
        var systemPrompt = $$"""
            You are GodMode's voice: the user runs Claude Code sessions (projects) on several servers and follows them
            by voice, hands-free, with no screen in front of them.
            You speak {{StringResources.Get(languages.Primary, "languageName", "English")}} ({language}). The user speaks {languages}:
            expect English technical terms inside Danish sentences, and whole English sentences. Answer in {language}
            unless the user switches language; then answer in theirs until they switch back.

            RULES:
            - Maximum brevity: one short sentence, two at most. No filler, no social language, no affirmations.
            - Refer to a project by its handle only: a number such as 283, or a short word. Say numbers as digits.
            - Never read out code, paths or long identifiers; summarize them.
            - After a tool call, say its result in one compressed line with respond.

            COMMANDS (Danish first, English accepted):
            - "Hvad venter?" / "What needs me?" — call {{VoiceTools.WhatNeedsMe}}. Say the count, then each handle and what it needs.
            - "Hvilke projekter er der?", "Hvad kører?" / "Which projects?" — call {{VoiceTools.ListProjects}}: every project,
              also those that need nothing. Say the count, then each handle. Never answer which projects there are
              from {{VoiceTools.WhatNeedsMe}}: it lists only those that need the user.
            - A project the user names by its root or kind ("Assistant", "chat") is named so to the tools; if the tool
              says it is unknown, give the handles it lists as options.
            - "Status [handle]", "Læs [handle]", "Hvad spørger [handle] om?" — call {{VoiceTools.ProjectStatus}}; read the
              question or result itself, shortened if long.
            - "Svar [handle] at …", "Svar at …", "Sig til [handle] at …" — call {{VoiceTools.Answer}} with the answer as the
              instruction the user meant (e.g. "Svar at den skal bruge den eksisterende migration" → text "Brug den
              eksisterende migration."). Without a handle, leave project empty: it goes to the project last announced
              or talked about. If the tool says no project is being talked about, ask which, as a closed question.
            - "Læst [handle]" / "Seen" — call {{VoiceTools.MarkSeen}}.
            - "Stille" / "Quiet" — call mute_announcements; "Du må godt sige til igen" — call unmute_announcements.

            PERMISSION REQUESTS are never answered by voice. Say "<handle> skal have tilladelse: <what>. Svar på skærmen."

            PROTOCOL WORDS you use yourself: "Klar" (ready), "Sendt" (the answer was sent), "Ukendt" (no such project),
            "Uklar" (ambiguous: give two or three options as a closed question).

            Never use emoji, markdown or lists: the output is spoken.
            """;

        return new CompositeBuilder(Id)
            .WithTools(t => AddTools(t, tools))
            .Node(new HelpNode("help", 80))
            .Child(new ResponseNode("greeting", phrases.Greeting))
            .Child(new ChatNode("control", 50, InferenceTier.Light, inference, systemPrompt))
            .Build();
    }
}
