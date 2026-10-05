using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #468: voice's lists say what is recent first, by when each session last did something (its last line, turn's
/// end or recap, not <see cref="ProjectSummary.UpdatedAt"/>, which every status write changes), and leave out what has
/// been stale a day (<see cref="VoiceSettings.StaleHours"/>), counted: "og 13 gamle". "alle" lists everything; a window
/// the user names ("den sidste time", "siden jeg sidst spurgte") lists what was active in it. What needs the user is
/// never left out as stale. The time voice last listed is the voice session's own.
/// </summary>
public sealed class LastActivityTests
{
    private const string ServerA = "server-a";
    private const int TwoDays = 2 * 24 * 60;

    private static readonly VoicePhrases English = new(new SessionLanguages("en-US"));

    private static (FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation) Tools(FakeServers? servers = null, TimeSpan? staleAfter = null)
    {
        servers ??= new FakeServers(ServerA);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        return (servers, new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation, staleAfter: staleAfter), conversation);
    }

    /// <summary>
    /// The issue's 16: 3 active in the last hour, 13 whose last line was two days ago. Every status was written just now
    /// (a server's restart writes them all), in the opposite order to their activity: UpdatedAt says nothing of it.
    /// </summary>
    private static void AddSixteen(FakeServers servers)
    {
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-1-a", "1-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 0, outputMinutesAgo: 50);
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-2-a", "2-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 2, outputMinutesAgo: 5);
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-3-a", "3-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 1, outputMinutesAgo: 20);
        for (var i = 11; i <= 23; i++)
            servers.AddProject(ServerA, $"Mega/GodMode/261002-issue-{i}-s", $"{i}-x", root: "GodMode", kind: "issue", profile: "Mega",
                state: ProjectState.Stopped, minutesAgo: 0, outputMinutesAgo: TwoDays + i);
    }

    /// <summary>The issue's test: the 3 named, the most recent first, and the stale ones counted, not named.</summary>
    [Fact]
    public async Task Of_sixteen_the_three_active_are_named_most_recent_first_and_the_stale_counted()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);

        var result = tools.ListProjectsText();

        Assert.Equal("3 projekter. Profil Mega, root GodMode: issue 2, issue 3, issue 1. Og 13 gamle.", conversation.TakeSaid(result));
        Assert.Contains("13 stale ones, with no activity in the last 24 hours, were left out and not named.", result);
        Assert.DoesNotContain("issue 11", result);
        Assert.StartsWith("Nothing more to read", await tools.ReadMoreAsync(CancellationToken.None));
    }

    /// <summary>"Alle": all 16, summarised by state (#457), the most recent first in each.</summary>
    [Fact]
    public async Task All_lists_the_stale_ones_too_the_most_recent_first_in_each_state()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);

        Assert.Equal("16 projekter. issue 2, issue 3 og issue 1 er idle, resten er stoppet. Mere?",
            conversation.TakeSaid(tools.ListProjectsText(since: VoiceTools.SinceAll)));
        Assert.Equal("Stoppet. Profil Mega, root GodMode: issue 11, issue 12, issue 13, issue 14, issue 15. Mere?",
            conversation.TakeSaid(await tools.ReadMoreAsync(CancellationToken.None)));
    }

    /// <summary>A session that needs the user is listed whatever its age: only the other stale ones are counted.</summary>
    [Fact]
    public void A_stale_session_that_needs_the_user_is_never_left_out()
    {
        var (servers, tools, conversation) = Tools();
        servers.Set(ServerA, FakeServers.Question("Mega/GodMode/261001-issue-101-q", "101-cleanup", "Slet kolonnerne?", minutesAgo: 3 * 24 * 60) with { Profile = "Mega", Root = "GodMode" });
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-2-a", "2-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 5);
        servers.AddProject(ServerA, "Mega/GodMode/261002-issue-11-s", "11-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped, outputMinutesAgo: TwoDays);

        Assert.Equal("2 projekter. Profil Mega, root GodMode: issue 2, issue 101 om cleanup. Og 1 gammel.", conversation.TakeSaid(tools.ListProjectsText()));
    }

    /// <summary>"Siden jeg sidst spurgte": only what was active after this voice session's last list.</summary>
    [Fact]
    public void Since_last_asked_lists_only_what_was_active_after_the_last_list()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);
        Assert.Null(conversation.LastListed);
        tools.ListProjectsText();
        Assert.NotNull(conversation.LastListed);

        // issue 3 writes after the list
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-3-a", "3-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 1, outputMinutesAgo: 0);

        var result = tools.ListProjectsText(since: VoiceTools.SinceLastAsked);
        Assert.Equal("1 projekt. Profil Mega, root GodMode: issue 3. Og 15 uden nyt.", conversation.TakeSaid(result));
        Assert.Contains("15 more had no activity since the user last asked", result);

        // Nothing since that list
        Assert.Equal("Intet nyt.", conversation.TakeSaid(tools.ListProjectsText(since: VoiceTools.SinceLastAsked)));
    }

    /// <summary>
    /// #507: a list of one root said nothing of the others, so "siden sidst" across all of them still counts from the last
    /// list of all, and takes in what another root did since; one of that root counts from its own.
    /// </summary>
    [Fact]
    public void A_list_of_one_root_moves_since_last_asked_for_that_root_alone()
    {
        var (servers, tools, conversation) = Tools();
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-2-a", "2-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 5);
        servers.AddProject(ServerA, "Private/voicebot/261004-issue-7-v", "7-x", root: "voicebot", kind: "issue", profile: "Private", outputMinutesAgo: 5);
        tools.ListProjectsText();

        // voicebot's issue 7 writes, then only GodMode is listed
        servers.AddProject(ServerA, "Private/voicebot/261004-issue-7-v", "7-x", root: "voicebot", kind: "issue", profile: "Private", outputMinutesAgo: 0);
        conversation.TakeSaid(tools.ListProjectsText("GodMode"));

        Assert.Equal("Intet nyt.", conversation.TakeSaid(tools.ListProjectsText("GodMode", since: VoiceTools.SinceLastAsked)));
        Assert.Equal("1 projekt. Profil Private, root voicebot: issue 7. Og 1 uden nyt.",
            conversation.TakeSaid(tools.ListProjectsText(since: VoiceTools.SinceLastAsked)));
    }

    /// <summary>
    /// #507: in a window the user asked for, a session that needs the user from before is left out as "uden nyt", and
    /// said to wait on them, as what needs me says "fra før".
    /// </summary>
    [Fact]
    public void A_window_says_that_one_left_out_needs_the_user()
    {
        var (servers, tools, conversation) = Tools();
        servers.Set(ServerA, FakeServers.Question("Mega/GodMode/261004-issue-101-q", "101-cleanup", "Slet kolonnerne?", minutesAgo: 90) with { Profile = "Mega", Root = "GodMode" });
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-2-a", "2-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 5);
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-3-a", "3-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 120);

        var result = tools.ListProjectsText(since: "30");
        Assert.Equal("1 projekt. Profil Mega, root GodMode: issue 2. Og 2 uden nyt. 1 af dem venter på dig.", conversation.TakeSaid(result));
        Assert.Contains("1 of them need the user from before", result);
        Assert.Equal("Intet nyt. 1 venter på dig fra før.", conversation.TakeSaid(tools.ListProjectsText(since: "1")));
        Assert.Equal("And 1 with nothing new. It needs you.", English.LeftOut(new LeftOut(1, Asked: true, Waiting: 1)));
    }

    /// <summary>The time is the voice session's own: another session that has listed nothing gets the default list, and is told so.</summary>
    [Fact]
    public void The_last_list_is_each_voice_sessions_own()
    {
        var servers = new FakeServers(ServerA);
        var (_, first, _) = Tools(servers);
        var (_, second, secondConversation) = Tools(servers);
        AddSixteen(servers);
        first.ListProjectsText();

        var result = second.ListProjectsText(since: VoiceTools.SinceLastAsked);
        Assert.Contains("Nothing was listed before in this voice session, so this is the default list", result);
        Assert.Equal("3 projekter. Profil Mega, root GodMode: issue 2, issue 3, issue 1. Og 13 gamle.", secondConversation.TakeSaid(result));
    }

    /// <summary>"Den sidste time": the model gives the minutes.</summary>
    [Fact]
    public void A_window_in_minutes_lists_what_was_active_in_it()
    {
        var (servers, tools, conversation) = Tools();
        AddSixteen(servers);

        Assert.Equal("2 projekter. Profil Mega, root GodMode: issue 2, issue 3. Og 14 uden nyt.", conversation.TakeSaid(tools.ListProjectsText(since: "30")));
        Assert.StartsWith("'i går' is no window. Nothing was listed.", tools.ListProjectsText(since: "i går"));
    }

    /// <summary>All stale: nothing named, the stale counted.</summary>
    [Fact]
    public void Nothing_active_says_nothing_new_and_counts_the_stale()
    {
        var (servers, tools, conversation) = Tools();
        servers.AddProject(ServerA, "Mega/GodMode/261002-issue-11-s", "11-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped, outputMinutesAgo: TwoDays);
        servers.AddProject(ServerA, "Mega/GodMode/261002-issue-12-s", "12-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Stopped, outputMinutesAgo: TwoDays);

        Assert.Equal("Intet nyt. 2 gamle.", conversation.TakeSaid(tools.ListProjectsText()));
    }

    /// <summary>A running session is active now, whatever its last line the list heard of.</summary>
    [Fact]
    public void A_running_session_is_active_now()
    {
        var (servers, tools, conversation) = Tools();
        servers.AddProject(ServerA, "Mega/GodMode/261002-issue-11-r", "11-x", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Running, outputMinutesAgo: TwoDays);

        Assert.Equal("1 projekt. Profil Mega, root GodMode: issue 11.", conversation.TakeSaid(tools.ListProjectsText()));
    }

    /// <summary>The staleness is a setting: an hour leaves out what was last active 2 hours ago.</summary>
    [Fact]
    public void The_stale_cut_is_the_settings()
    {
        var (servers, tools, conversation) = Tools(staleAfter: TimeSpan.FromHours(1));
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-2-a", "2-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 5);
        servers.AddProject(ServerA, "Mega/GodMode/261004-issue-3-a", "3-x", root: "GodMode", kind: "issue", profile: "Mega", outputMinutesAgo: 120);

        Assert.Equal("1 projekt. Profil Mega, root GodMode: issue 2. Og 1 gammel.", conversation.TakeSaid(tools.ListProjectsText()));
    }

    /// <summary>What needs the user, the most recent first; a window leaves out the older and counts them "fra før".</summary>
    [Fact]
    public async Task What_needs_me_is_the_most_recent_first_and_a_window_counts_the_older()
    {
        var (servers, tools, conversation) = Tools();
        servers.Set(ServerA,
            FakeServers.Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 90),
            FakeServers.Question("p/r/283", "283-voice", "Skal jeg pushe?", minutesAgo: 3));

        Assert.Equal("2 venter på dig: issue 283, voice, har et spørgsmål. issue 101, cleanup, har et spørgsmål.",
            conversation.TakeSaid(await tools.WhatNeedsMeAsync(CancellationToken.None)));
        Assert.Equal("issue 283, voice, har et spørgsmål. Og 1 fra før.",
            conversation.TakeSaid(await tools.WhatNeedsMeAsync(CancellationToken.None, since: "60")));
        Assert.Equal("Intet nyt venter. 2 venter fra før.",
            conversation.TakeSaid(await tools.WhatNeedsMeAsync(CancellationToken.None, since: VoiceTools.SinceLastAsked)));
    }

    [Fact]
    public void The_left_out_in_English()
    {
        Assert.Equal("3 projects. Profile Mega, root GodMode: issue 2, issue 3, issue 1. And 13 old ones.",
            English.Projects([new("Mega", "GodMode", ["issue 2", "issue 3", "issue 1"])], new LeftOut(13, Asked: false)));
        Assert.Equal("Nothing new. 1 old one.", English.NothingNew(new LeftOut(1, Asked: false)));
        Assert.Equal("Nothing new.", English.NothingNew(new LeftOut(4, Asked: true)));
        Assert.Equal("16 projects. 3 are idle, 13 are stopped. And 2 with nothing new. More?",
            English.ListSummary(16, [(ListedState.Idle, 3, []), (ListedState.Stopped, 13, [])], more: true, new LeftOut(2, Asked: true)));
        Assert.Equal("Nothing new needs you. 1 needs you from before.", English.WaitingFromBefore(1, saidAny: false));
    }

}
