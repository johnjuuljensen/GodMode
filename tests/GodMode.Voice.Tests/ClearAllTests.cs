using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// "Ryd notifikationerne" (#532): every item that only tells the user something (finished, idle, failed, blocked,
/// changes requested) is marked seen in one call, and what waits on an answer (a question, a permission, a decision)
/// is left, and said, each in one short line. "Hvad venter" says several a short line each.
/// </summary>
public sealed class ClearAllTests
{
    private const string ServerA = "server-a";

    [Fact]
    public async Task Everything_that_only_tells_is_marked_seen_and_what_waits_on_an_answer_is_said()
    {
        var servers = new FakeServers(ServerA);
        var (tools, conversation) = Tools(servers);
        servers.Set(ServerA,
            Finished("p/r/525", "525-voice", "Merged."),
            Finished("p/r/526", "526-voice", "Waiting on CI.", outcome: null),
            Question("p/r/527", "527-voice", "No access.") with { Outcome = TurnOutcome.Blocked },
            CreateFailed("p/r/528", "528-voice", "Script failed."),
            Question("p/r/86", "86-fe", "Which title?"),
            Permission("p/r/87", "87-fe", "Bash: rm -rf build"));

        var result = await tools.MarkAllSeenAsync(null, default);

        Assert.Equal(["p/r/525", "p/r/526", "p/r/527", "p/r/528"], servers.Seen.Select(p => p.ProjectId).Order());
        Assert.Equal("Ryddet 4. Tilbage: issue 86 har et spørgsmål. issue 87 skal have tilladelse: Bash: rm -rf build. Svar på skærmen.",
            conversation.TakeSaid(result));
    }

    [Fact]
    public async Task Nothing_to_clear_says_so()
    {
        var servers = new FakeServers(ServerA);
        var (tools, conversation) = Tools(servers);
        servers.Set(ServerA, Question("p/r/86", "86-fe", "Which title?"));

        var result = await tools.MarkAllSeenAsync(null, default);

        Assert.Empty(servers.Seen);
        Assert.Equal("Intet at rydde. Tilbage: issue 86 har et spørgsmål.", conversation.TakeSaid(result));
    }

    /// <summary>Several waiting are said a short line each, never their own words (#532): those are read when the user asks about one.</summary>
    [Fact]
    public async Task What_needs_me_says_several_a_short_line_each()
    {
        var servers = new FakeServers(ServerA);
        var (tools, conversation) = Tools(servers);
        servers.Set(ServerA,
            Finished("p/r/525", "525-voice", "Merged.", minutesAgo: 3) with { Spoken = "Pull request 531 er merget, og alt er grønt." },
            Question("p/r/526", "526-voice", "No access.", minutesAgo: 2) with { Outcome = TurnOutcome.Blocked, Spoken = "Jeg mangler adgang til repoet." },
            Question("p/r/86", "86-fe", "Which title?", minutesAgo: 1));

        var result = await tools.WhatNeedsMeAsync(default);

        var said = conversation.TakeSaid(result)!;
        Assert.StartsWith("3 venter på dig: issue 86 har et spørgsmål. issue 526, voice, er blokeret. issue 525", said);
        Assert.EndsWith("er færdig.", said);
        Assert.DoesNotContain("adgang", said);
        Assert.DoesNotContain("merget", said);
    }

    private static (VoiceTools Tools, VoiceConversation Conversation) Tools(FakeServers servers)
    {
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        return (new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation,
            phrases: new VoicePhrases(new SessionLanguages("da-DK"))), conversation);
    }
}
