using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #457: a project list past a handful is summarised by state (needs you, running, idle, stopped), and the projects
/// the summary did not name are read on "mere", <see cref="ProjectListing.Page"/> at a time, each page ending in "Mere?"
/// while more follows. Seen 2026-10-03 01:50: 16 handles in a row, 15 s of speech. Both are said by the code, so a long
/// list is still one model call, and each page another (#456). "Mere" reads on in what was read in parts last: one
/// cursor for a list and a reply, and a list said later replaces it.
/// </summary>
public sealed class ListPagesTests
{
    private const string ServerA = "server-a";

    private static readonly VoicePhrases English = new(new SessionLanguages("en-US"));

    /// <summary>The issue's list: 15 stopped issues, the one changed last first (issue 1), and branch master idle.</summary>
    private static void AddSixteen(FakeServers servers)
    {
        for (var i = 1; i <= 15; i++)
            servers.AddProject(ServerA, $"Mega/GodMode/260930-issue-{i}-s", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega",
                state: ProjectState.Stopped, minutesAgo: i);
        servers.AddProject(ServerA, "Mega/GodMode/260930-branch-master-m", "master", root: "GodMode", kind: "branch", profile: "Mega", minutesAgo: 30);
    }

    private static (FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation) Tools()
    {
        var servers = new FakeServers(ServerA);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        return (servers, new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation), conversation);
    }

    /// <summary>The issue's test: the first answer names the idle one and counts the rest; "mere" reads the next page.</summary>
    [Fact]
    public async Task Sixteen_projects_one_idle_are_summarised_and_mere_reads_the_next_page()
    {
        var servers = new FakeServers();
        AddSixteen(servers);
        var model = new ScriptedChatClient().CallTool(VoiceTools.ListProjects).CallTool(VoiceTools.ReadMore);
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("List GodMode-sessionerne.");
        await voice.Events.SaidAsync("16 projekter. branch master er idle, resten er stoppet. Mere?");
        voice.Transcriptions.SayAsRecognized("Mere.");
        await voice.Events.SaidAsync("Stoppet. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5. Mere?");

        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task The_pages_run_to_the_end_and_the_last_asks_for_no_more()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);

        Assert.Equal("16 projekter. branch master er idle, resten er stoppet. Mere?", conversation.TakeSaid(tools.ListProjectsText()));
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5. Mere?", await SaidOnMoreAsync(tools, conversation));
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 6, issue 7, issue 8, issue 9, issue 10. Mere?", await SaidOnMoreAsync(tools, conversation));
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 11, issue 12, issue 13, issue 14, issue 15.", await SaidOnMoreAsync(tools, conversation));

        var end = await tools.ReadMoreAsync(CancellationToken.None);
        Assert.StartsWith("Nothing more to read: the project list was read to its end.", end);
        Assert.Null(conversation.TakeSaid(end));
    }

    /// <summary>
    /// States in order, each named while it fits in a page, else counted: 2 need you (named), 4 run (too many for the 3
    /// left: counted), 10 stopped (counted). The pages read the running ones first, then the stopped, each state said
    /// before its first.
    /// </summary>
    [Fact]
    public async Task Each_state_is_named_while_it_fits_and_counted_after()
    {
        var (servers, tools, conversation) = Tools();
        servers.Set(ServerA, InMega("260930-issue-101-q", "101-cleanup", AttentionKind.Question), InMega("260930-issue-283-f", "283-voice", AttentionKind.Finished));
        for (var i = 1; i <= 4; i++)
            servers.AddProject(ServerA, $"Mega/GodMode/260930-issue-{i}-r", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Running, minutesAgo: i);
        for (var i = 11; i <= 20; i++)
            servers.AddProject(ServerA, $"Mega/GodMode/260930-issue-{i}-s", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped, minutesAgo: i);

        var said = conversation.TakeSaid(tools.ListProjectsText());
        Assert.NotNull(said);
        Assert.Matches(@"^16 projekter\. issue (101|283) og issue (101|283) venter på dig, 4 kører, 10 er stoppet\. Mere\?$", said);
        Assert.Equal("Kører. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4. Stoppet. Profil Mega, root GodMode: issue 11. Mere?",
            await SaidOnMoreAsync(tools, conversation));
    }

    /// <summary>Five are said whole, as before: no summary, and nothing for "mere".</summary>
    [Fact]
    public async Task A_list_of_a_page_or_less_is_said_whole()
    {
        var (servers, tools, conversation) = Tools();
        for (var i = 1; i <= ProjectListing.Page; i++)
            servers.AddProject(ServerA, $"Mega/GodMode/260930-issue-{i}-s", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped, minutesAgo: i);

        Assert.Equal("5 projekter. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5.", conversation.TakeSaid(tools.ListProjectsText()));
        Assert.StartsWith("Nothing more to read", await tools.ReadMoreAsync(CancellationToken.None));
    }

    /// <summary>A list said later replaces the pages of the one before: a short one leaves none, a long one starts its own.</summary>
    [Fact]
    public async Task A_later_list_replaces_the_pages_of_an_earlier_one()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);
        servers.AddProject(ServerA, "Private/voicebot/260930-branch-main-p", "main", root: "voicebot", kind: "branch", profile: "Private");

        Assert.Equal("17 projekter. branch master i GodMode, profil Mega og branch main i voicebot, profil Private er idle, resten er stoppet. Mere?",
            conversation.TakeSaid(tools.ListProjectsText()));
        conversation.TakeSaid(tools.ListProjectsText("Private"));
        Assert.StartsWith("Nothing more to read", await tools.ReadMoreAsync(CancellationToken.None));

        conversation.TakeSaid(tools.ListProjectsText());
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5. Mere?", await SaidOnMoreAsync(tools, conversation));
        conversation.TakeSaid(tools.ListProjectsText("Mega"));
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5. Mere?", await SaidOnMoreAsync(tools, conversation));
    }

    /// <summary>One cursor: "mere" reads on in what was read in parts last, a list after a reply, a reply after a list.</summary>
    [Fact]
    public async Task Mere_reads_on_in_what_was_read_in_parts_last()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);
        var reply = string.Concat(Enumerable.Range(1, 60).Select(i => $"Trin {i}: relayet startede og forbandt uden fejl. ")).Trim();
        servers.SetReplies(ServerA, "Mega/GodMode/260930-branch-master-m", new AssistantReply(reply, true));

        Assert.Contains("[Part 1 of", await tools.ReadReplyAsync("master", null, CancellationToken.None));
        conversation.TakeSaid(tools.ListProjectsText());
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 1, issue 2, issue 3, issue 4, issue 5. Mere?", await SaidOnMoreAsync(tools, conversation));

        await tools.ReadReplyAsync("master", null, CancellationToken.None);
        Assert.StartsWith("branch master's reply, part 2 of", await tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public void The_summary_and_pages_in_English()
    {
        Assert.Equal("16 projects. branch master is idle, the rest are stopped. More?",
            English.ListSummary(16, [(ListedState.Idle, 1, [new SpokenName("branch master")]), (ListedState.Stopped, 15, [])], more: true));
        Assert.Equal("8 projects. 6 need you, issue 1 and issue 2 are running.",
            English.ListSummary(8, [(ListedState.NeedsYou, 6, []), (ListedState.Running, 2, [new SpokenName("issue 1"), new SpokenName("issue 2")])], more: false));
        Assert.Equal("7 projects. 1 needs you, 6 are stopped. More?",
            English.ListSummary(7, [(ListedState.NeedsYou, 1, []), (ListedState.Stopped, 6, [])], more: true));
    }

    private static AttentionItem InMega(string id, string name, AttentionKind kind) =>
        new($"Mega/GodMode/{id}", name, "Mega", "GodMode", kind, DateTime.UtcNow.AddMinutes(-5), "Slet kolonnerne?");

    /// <summary>What the code says of the next part "mere" reads; fails when the model would say it.</summary>
    private static async Task<string> SaidOnMoreAsync(VoiceTools tools, VoiceConversation conversation)
    {
        var result = await tools.ReadMoreAsync(CancellationToken.None);
        return conversation.TakeSaid(result) ?? throw new Xunit.Sdk.XunitException($"read_more was not said by the code: {result}");
    }
}
