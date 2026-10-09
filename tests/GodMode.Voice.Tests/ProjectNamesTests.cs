using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice names the profile and root (issue #450): lists are grouped by them, a session named alone gets its root, and
/// its profile when another profile has a root of that name or it is not in the profile spoken of last, and its handle
/// says what it is ("issue 376", "branch master").
/// </summary>
public sealed class ProjectNamesTests
{
    private const string Server = "server-a";
    private static readonly ProjectRef GodmodeMaster = new(Server, "Godmode/GodMode/260930-branch-master-a1");
    private static readonly ProjectRef MegaMaster = new(Server, "Mega/GodMode/260930-branch-master-b2");

    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));
    private static readonly VoicePhrases English = new(new SessionLanguages("en-US"));

    private sealed record Voice(FakeServers Servers, ProjectHandles Handles, AttentionBoard Board, VoiceTools Tools);

    private static Voice Start(params (ProjectRef Project, string Name, string Root, string Kind, string Profile)[] sessions)
    {
        var servers = new FakeServers(Server);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var tools = new VoiceTools(servers, board, projects, handles, new VoiceConversation());
        // The first given the most recent, so lists, which say the most recent first (#468), keep the order given
        foreach (var ((project, name, root, kind, profile), i) in sessions.Select((s, i) => (s, i)))
            servers.AddProject(Server, project.ProjectId, name, root: root, kind: kind, profile: profile, minutesAgo: i);
        return new Voice(servers, handles, board, tools);
    }

    /// <summary>Two profiles, each with a root named GodMode, each with a session named "master".</summary>
    private static Voice TwoMasters() => Start(
        (GodmodeMaster, "master", "GodMode", "branch", "Godmode"),
        (MegaMaster, "master", "GodMode", "branch", "Mega"));

    [Fact]
    public void The_list_groups_two_masters_by_their_profiles()
    {
        var voice = TwoMasters();

        Assert.Equal(
            "2 projects, in 2 groups by profile and root:\n" +
            "Profile Godmode, root GodMode (1 project):\n- branch master (master, branch): Idle\n" +
            "Profile Mega, root GodMode (1 project):\n- branch master (master, branch): Idle\n" +
            "Say the count, 2, then each group once, by its profile and root, with its projects by the names given here. " +
            "If you leave any out, say how many and why.",
            voice.Tools.ListProjectsText());
    }

    [Fact]
    public void The_announcement_of_each_master_says_its_profile()
    {
        var voice = TwoMasters();
        List<string> danish = [], english = [];
        voice.Board.Attach((item, _) =>
        {
            var name = voice.Tools.Names.Of(item.Project)!;
            danish.Add(Danish.Announce(name, item.Item));
            english.Add(English.Announce(name, item.Item));
        });

        voice.Servers.PushAttention(Server,
            Question(GodmodeMaster.ProjectId, "master", "Skal jeg pushe?", minutesAgo: 10),
            Question(MegaMaster.ProjectId, "master", "Skal jeg merge?", minutesAgo: 5));

        // Each time, though the profile is the one spoken of last: another profile has a GodMode too
        Assert.Equal(["branch master i GodMode, profil Godmode, har et spørgsmål", "branch master i GodMode, profil Mega, har et spørgsmål"], danish);
        Assert.Equal(["branch master in GodMode, profile Godmode, has a question", "branch master in GodMode, profile Mega, has a question"], english);
    }

    [Fact]
    public async Task The_status_of_each_master_says_which_it_is()
    {
        var voice = TwoMasters();

        Assert.StartsWith("branch master in GodMode, profile Mega (master, branch): Idle.",
            await voice.Tools.ProjectStatusAsync("branch master i GodMode, profil Mega", CancellationToken.None));
        Assert.Equal(MegaMaster, voice.Tools.Conversation.Current);
        Assert.StartsWith("branch master in GodMode, profile Godmode (master, branch): Idle.",
            await voice.Tools.ProjectStatusAsync("branch master i GodMode, profil Godmode", CancellationToken.None));
        Assert.Equal(GodmodeMaster, voice.Tools.Conversation.Current);
        Assert.StartsWith("branch master in GodMode, profile Mega (",
            await voice.Tools.ProjectStatusAsync("master i Mega", CancellationToken.None));

        // Said alone, "branch master" is either: nothing is read, and the user is asked which
        Assert.Equal("'branch master' names 2 projects: branch master in GodMode, profile Godmode; branch master in GodMode, profile Mega. " +
            "Nothing was done: ask which, as a closed question naming each by its root and profile.",
            await voice.Tools.ProjectStatusAsync("branch master", CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_to_one_master_goes_to_it_and_its_send_says_which()
    {
        var voice = TwoMasters();

        Assert.StartsWith("Sent to branch master in GodMode, profile Mega:",
            await voice.Tools.AnswerAsync("branch master i Mega", "Fortsæt.", CancellationToken.None));

        Assert.Equal(MegaMaster, Assert.Single(voice.Servers.Replies).Project);
        Assert.Equal("Sendt til branch master i GodMode, profil Mega.", Danish.Sent(voice.Tools.Conversation.TakeSent()));
    }

    [Fact]
    public async Task A_session_alone_gets_its_root_and_its_profile_when_it_changes()
    {
        var issue = new ProjectRef(Server, "Godmode/GodMode/260930-feat-376-voice-names-x1");
        var master = new ProjectRef(Server, "Private/voicebot/260930-branch-master-y2");
        var voice = Start(
            (issue, "feat-376-voice-names", "GodMode", "feat", "Godmode"),
            (master, "master", "voicebot", "branch", "Private"));
        var names = voice.Tools.Names;

        // New: all of its anchor (#455), its topic too, its root and profile of one name said once (#529); then the same
        // project again: its label alone
        Assert.Equal("issue 376, voice names i GodMode", Danish.Named(names.Of(issue)!));
        Assert.Equal("issue 376", Danish.Named(names.Of(issue)!));
        Assert.Equal("branch master i voicebot, profil Private", Danish.Named(names.Of(master)!));
        // Back to it after another: its topic, and the root and profile it changed back to
        Assert.Equal("issue 376, voice names in GodMode", English.Named(names.Of(issue)!));

        // Said as it is named, it is found
        Assert.Equal(issue, voice.Handles.Resolve("issue 376"));
        Assert.Equal(master, voice.Handles.Resolve("branch master"));
        Assert.Equal(master, voice.Handles.Resolve("master i voicebot"));
    }

    [Fact]
    public void One_root_says_no_root_and_one_profile_no_profile()
    {
        var voice = Start(
            (new ProjectRef(Server, "Godmode/GodMode/260930-feat-376-a"), "feat-376-a", "GodMode", "feat", "Godmode"),
            (new ProjectRef(Server, "Godmode/GodMode/260930-feat-382-b"), "feat-382-b", "GodMode", "feat", "Godmode"));

        Assert.Equal("issue 376", Danish.Named(voice.Tools.Names.Of(new ProjectRef(Server, "Godmode/GodMode/260930-feat-376-a"))!));
        Assert.StartsWith("2 projects, all in one group:\nProfile Godmode, root GodMode (2 projects):\n", voice.Tools.ListProjectsText());
    }

    [Theory]
    [InlineData("376", "feat-376-x", "feat", "issue 376")]
    [InlineData("master", "master", "branch", "branch master")]
    [InlineData("branch", "master", "branch", "branch master")]
    [InlineData("master 2", "master", "branch", "branch master")]
    [InlineData("testing", "testing", "chat", "chat testing")]
    [InlineData("chat", "chat", "chat", "chat")]
    [InlineData("chat 2", "chat", "chat", "chat 2")]
    [InlineData("vonage", "vonage", null, "vonage")]
    public void A_label_says_what_the_handle_is(string handle, string name, string? kind, string label) =>
        Assert.Equal(label, ProjectHandles.Label(handle, name, kind));

    /// <summary>A root and profile of one name are said once (#529: "in GodMode, profile Godmode" on every announcement).</summary>
    [Fact]
    public void A_root_with_its_profiles_name_is_said_once()
    {
        var overseer = new ProjectRef(Server, "Godmode/GodMode/260930-overseer-voice-epics-x1");
        var other = new ProjectRef(Server, "Mega/Mega-Assistant/260930-chat-general-y2");
        var voice = Start(
            (overseer, "voice-epics", "GodMode", "overseer", "Godmode"),
            (other, "general", "Mega-Assistant", "chat", "Mega"));

        Assert.Equal("overseer voice, voice epics in GodMode", English.Named(voice.Tools.Names.Full(overseer)!));
        Assert.Equal("chat general in Mega-Assistant, profile Mega", English.Named(voice.Tools.Names.Full(other)!));
    }

    /// <summary>Two profiles with a root of that name: the profile is said, which tells them apart.</summary>
    [Fact]
    public void A_root_two_profiles_have_still_says_its_profile()
    {
        var voice = TwoMasters();

        Assert.Equal("branch master in GodMode, profile Godmode", English.Named(voice.Tools.Names.Full(GodmodeMaster)!));
    }
}
