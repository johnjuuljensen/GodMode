using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Progressive disclosure (#455): the first line is short and anchored, and "Mere?", "Hvorfor?" or "Hvad er det?"
/// (<see cref="VoiceTools.ReadMore"/>) expand what was said last a step: an announcement of one project into its status,
/// its status into its last reply, a reply or a list into its next part.
/// </summary>
public sealed class DisclosureTests
{
    private const string Server = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");
    private static readonly ProjectRef P283 = new(Server, "p/r/283");
    private const string Asked = "Skal jeg bruge den eksisterende migration?";
    private const string Reply = "Jeg har to migrationer. Skal jeg bruge den eksisterende migration?";

    private sealed record Voice(FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation, GodModeAnnouncementFormatter Formatter, AttentionBoard Board);

    private static Voice Start()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation();
        var phrases = new VoicePhrases(Danish);
        var tools = new VoiceTools(servers, board, projects, handles, conversation, phrases: phrases);
        servers.Set(Server, Question(P283.ProjectId, "283-voice", Asked), Question("p/r/101", "101-cleanup", "Slet kolonnerne?"));
        servers.SetReplies(Server, P283.ProjectId, new AssistantReply(Reply, true));
        return new Voice(servers, tools, conversation, new GodModeAnnouncementFormatter(phrases, conversation, board, tools.Names), board);
    }

    private static void Announce(Voice voice, params string[] projectIds) =>
        voice.Formatter.Format([.. projectIds.Select(id => voice.Board.AnnouncementOf(voice.Board.ItemOf(new ProjectRef(Server, id))!, "x"))], Danish);

    [Fact]
    public async Task An_announcement_expands_into_its_status_then_its_last_reply()
    {
        var voice = Start();
        Announce(voice, P283.ProjectId);

        var status = await voice.Tools.ReadMoreAsync(CancellationToken.None);
        Assert.StartsWith("issue 283 (283-voice): WaitingInput. Needs the user: question: " + Asked, status);
        Assert.Equal($"issue 283 spørger: {Asked}", voice.Conversation.TakeSaid(status));

        Assert.StartsWith($"issue 283 (283-voice): WaitingInput. Last reply: {Reply}", await voice.Tools.ReadMoreAsync(CancellationToken.None));
        Assert.StartsWith("Nothing more to read", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task What_needs_me_naming_one_project_expands_into_its_status()
    {
        var voice = Start();
        voice.Servers.Set(Server, Question(P283.ProjectId, "283-voice", Asked));
        await voice.Tools.WhatNeedsMeAsync(CancellationToken.None);

        Assert.StartsWith("issue 283 (283-voice): WaitingInput.", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_status_expands_into_its_last_reply()
    {
        var voice = Start();
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);
        await voice.Tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.StartsWith($"issue 283 (283-voice): WaitingInput. Last reply: {Reply}", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    /// <summary>The last line is what is expanded: an announcement after a long list's first page takes "mere" from it.</summary>
    [Fact]
    public async Task The_last_line_is_expanded_not_a_list_read_before_it()
    {
        var voice = Start();
        for (var i = 1; i <= 12; i++)
            voice.Servers.AddProject(Server, $"Mega/GodMode/261004-issue-{i}-s", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped);
        voice.Tools.ListProjectsText(since: VoiceTools.SinceAll);
        Assert.IsType<ListReading>(voice.Conversation.Reading);

        Announce(voice, P283.ProjectId);

        Assert.StartsWith("issue 283 (283-voice)", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Several_announced_together_leave_what_was_read_before()
    {
        var voice = Start();
        await voice.Tools.ProjectStatusAsync("283", CancellationToken.None);

        Announce(voice, P283.ProjectId, "p/r/101");

        // Its reply, and named with its topic: 101 was named after it
        Assert.StartsWith("issue 283, voice (283-voice): WaitingInput. Last reply:", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_project_gone_since_its_line_has_nothing_more()
    {
        var voice = Start();
        Announce(voice, P283.ProjectId);
        voice.Servers.DeleteProject(Server, P283.ProjectId);

        Assert.StartsWith("Nothing more to read: the project the last line was about is gone.", await voice.Tools.ReadMoreAsync(CancellationToken.None));
    }

    /// <summary>By voice: "Hvad er det?" after an announcement is the announced project's question, said by the code, its label alone.</summary>
    [Fact]
    public async Task Hvad_er_det_after_an_announcement_says_its_question()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.ReadMore);
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        servers.Set(Server, Question(P283.ProjectId, "283-voice", Asked));
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Hvad er det?");

        await voice.Events.SaidAsync($"issue 283 spørger: {Asked}");
        Assert.Equal(1, model.Calls);
    }
}
