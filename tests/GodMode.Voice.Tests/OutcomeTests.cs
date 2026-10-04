using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice says what the session declared (issue #467): "færdig" only for a turn it said is done, idle for a finished turn
/// with no outcome, and blocked for a question it said it is blocked on.
/// </summary>
public sealed class OutcomeTests
{
    private const string ServerA = "server-a";
    private const string Id = "p/r/283";
    private const string Spoken = "Pull requesten er åbnet, og testene er grønne.";
    private static readonly SpokenName Issue = new("issue 283");
    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));
    private static readonly VoicePhrases English = new(new SessionLanguages("en-US"));

    [Fact]
    public void A_done_turn_is_said_as_done()
    {
        Assert.Equal("issue 283 er færdig", Danish.Announce(Issue, Finished(Id, "283-voice", "Done.")));
        Assert.Equal("issue 283 is done", English.Announce(Issue, Finished(Id, "283-voice", "Done.")));
        Assert.Equal($"issue 283 er færdig: {Spoken}", Danish.Announce(Issue, Finished(Id, "283-voice", "Done.") with { Spoken = Spoken }));
    }

    [Fact]
    public void A_turn_with_no_outcome_is_said_as_idle_never_as_done()
    {
        var idle = Finished(Id, "283-voice", "Venter på CI.", outcome: null);

        Assert.Equal("issue 283 er idle", Danish.Announce(Issue, idle));
        Assert.Equal("issue 283 is idle", English.Announce(Issue, idle));
        Assert.Equal($"issue 283 er idle: {Spoken}", Danish.Announce(Issue, idle with { Spoken = Spoken }));
        Assert.DoesNotContain("færdig", Danish.Announce(Issue, idle with { Spoken = Spoken }));
    }

    [Fact]
    public void A_blocked_question_is_said_as_blocked()
    {
        var blocked = Question(Id, "283-voice", "Jeg mangler adgang til repoet.") with { Outcome = TurnOutcome.Blocked };

        Assert.Equal("issue 283 er blokeret", Danish.Announce(Issue, blocked));
        Assert.Equal("issue 283 is blocked", English.Announce(Issue, blocked));
        Assert.Equal($"issue 283 er blokeret: {Spoken}", Danish.Announce(Issue, blocked with { Spoken = Spoken }));
        Assert.Equal("issue 283 har et spørgsmål", Danish.Announce(Issue, blocked with { Outcome = TurnOutcome.NeedsYou }));
    }

    /// <summary>What the model reads of a finished turn says whether the session said it is done.</summary>
    [Fact]
    public async Task What_needs_me_says_done_only_for_a_done_turn()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var tools = new VoiceTools(servers, board, projects, handles, new VoiceConversation());
        servers.AddProject(ServerA, "done", "done one", root: "GodMode", kind: "chat");
        servers.AddProject(ServerA, "idle", "idle one", root: "GodMode", kind: "chat");
        servers.PushAttention(ServerA,
            Finished("done", "done one", "Merged.", minutesAgo: 10),
            Finished("idle", "idle one", "Waiting on CI.", outcome: null));

        var said = await tools.WhatNeedsMeAsync(CancellationToken.None);

        Assert.Contains("finished: Merged.", said);
        Assert.Contains("idle (it did not say it is done): Waiting on CI.", said);
    }
}
