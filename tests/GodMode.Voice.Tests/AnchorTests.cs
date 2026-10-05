using GodMode.Shared.Enums;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Brief in words, never in context (#455): each line names the project it is about as much as what was said before it
/// leaves the user needing. Its label alone for the project the last line was about; its topic, and its root and profile
/// when they changed, for another; all of it for one not named in <see cref="VoiceConversation.FullAnchorAfter"/>, or with
/// <see cref="VoiceConversation.FullAnchorAfterOthers"/> others named since.
/// </summary>
public sealed class AnchorTests
{
    private const string Server = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");
    private static readonly VoicePhrases Phrases = new(Danish);
    private static readonly ProjectRef Mic = new(Server, "Mega/GodMode/261004-feat-283-a1");
    private static readonly ProjectRef Columns = new(Server, "Mega/GodMode/261004-feat-101-b2");
    private static readonly ProjectRef Master = new(Server, "Private/voicebot/261004-branch-master-c3");
    private static readonly ProjectRef Backup = new(Server, "Mega/Assistant/261004-chat-backup-d4");

    private sealed record Voice(FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation, AttentionBoard Board, ManualTime Time)
    {
        /// <summary>The project as a line said now names it.</summary>
        public string Said(ProjectRef project) => Phrases.Named(Tools.Names.Of(project)!);
    }

    /// <summary>Two issues in GodMode and a chat in Assistant, profile Mega; a branch in voicebot, profile Private.</summary>
    private static Voice Start()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var time = new ManualTime();
        var conversation = new VoiceConversation(time);
        var tools = new VoiceTools(servers, board, projects, handles, conversation, time, Phrases);
        servers.AddProject(Server, Mic.ProjectId, "Issue #283: Voice: mic timeout, on headsets", root: "GodMode", kind: "feat", profile: "Mega");
        servers.AddProject(Server, Columns.ProjectId, "Issue #101: Drop the old columns", root: "GodMode", kind: "feat", profile: "Mega");
        servers.AddProject(Server, Master.ProjectId, "master", root: "voicebot", kind: "branch", profile: "Private");
        servers.AddProject(Server, Backup.ProjectId, "chat_backup job", root: "Assistant", kind: "chat", profile: "Mega");
        return new Voice(servers, tools, conversation, board, time);
    }

    [Theory]
    [InlineData("Issue #455: Voice: brief in words, never in context — anchor each line to its project", "feat", "issue 455", "brief in words")]
    [InlineData("Issue #456: Voice: the code says list and status results itself", "feat", "issue 456", "code says list")]
    [InlineData("Issue #284: Epic: Voice in the app, Danish first", "epic", "issue 284", "Voice in the app")]
    [InlineData("283-mic-timeout", null, "issue 283", "mic timeout")]
    [InlineData("feat-376-voice-names", "feat", "issue 376", "voice names")]
    [InlineData("chat_backup job", "chat", "chat backup", "backup job")]
    // Nothing the label does not say already, or nothing at all
    [InlineData("branch_master", "branch", "branch master", null)]
    [InlineData("voice-epics", "epic", "epic voice", null)]
    [InlineData("issue_41", "issue", "issue 41", null)]
    [InlineData("102-b", null, "issue 102", null)]
    public void A_topic_is_a_few_words_of_the_title_or_name(string name, string? kind, string label, string? topic) =>
        Assert.Equal(topic, ProjectTopics.Of(name, kind, label));

    [Fact]
    public void The_windows_are_ten_minutes_and_two_other_projects() =>
        Assert.Equal((TimeSpan.FromMinutes(10), 2), (VoiceConversation.FullAnchorAfter, VoiceConversation.FullAnchorAfterOthers));

    [Fact]
    public void A_project_not_named_before_gets_its_full_anchor()
    {
        var voice = Start();

        Assert.Equal("issue 283, mic timeout i GodMode, profil Mega", voice.Said(Mic));
    }

    [Fact]
    public void The_same_project_again_is_its_label_alone()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal("issue 283", voice.Said(Mic));
    }

    [Fact]
    public void Back_from_another_in_its_root_it_is_its_label_and_topic()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Said(Columns);

        Assert.Equal("issue 283, mic timeout", voice.Said(Mic));
    }

    [Fact]
    public void Back_from_another_root_and_profile_it_says_them()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Said(Master);

        Assert.Equal("issue 283, mic timeout i GodMode, profil Mega", voice.Said(Mic));
    }

    [Fact]
    public void After_two_other_projects_it_gets_its_full_anchor_again()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Said(Backup);
        voice.Said(Columns);

        // One other since would be its label and topic alone, as Columns is in its root and profile
        Assert.Equal("issue 283, mic timeout i GodMode, profil Mega", voice.Said(Mic));
    }

    [Fact]
    public void After_a_long_silence_the_same_project_gets_its_full_anchor_again()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Time.Advance(VoiceConversation.FullAnchorAfter);

        Assert.Equal("issue 283, mic timeout i GodMode, profil Mega", voice.Said(Mic));
    }

    [Fact]
    public void A_project_not_named_for_a_while_gets_its_full_anchor_again_with_one_other_between()
    {
        var voice = Start();
        voice.Said(Mic);
        voice.Time.Advance(VoiceConversation.FullAnchorAfter);
        voice.Said(Columns);

        Assert.Equal("issue 283, mic timeout i GodMode, profil Mega", voice.Said(Mic));
    }

    /// <summary>The model is given the anchor to use: the name in a tool's text is the anchored one.</summary>
    [Fact]
    public async Task A_tools_text_gives_the_model_the_anchor_to_use()
    {
        var voice = Start();

        Assert.StartsWith("issue 283, mic timeout in GodMode, profile Mega (", await voice.Tools.ProjectStatusAsync("283", CancellationToken.None));
        Assert.StartsWith("issue 283 (", await voice.Tools.ProjectStatusAsync("283", CancellationToken.None));
        Assert.StartsWith("Sent to issue 283:", await voice.Tools.AnswerAsync("283", "Ja.", CancellationToken.None));
    }

    /// <summary>
    /// An announcement is worded when it is said, not when it was queued: a status read of its project before it was said
    /// leaves it the label alone, and one of another project its topic.
    /// </summary>
    [Fact]
    public async Task An_announcement_is_anchored_by_what_was_said_before_it_not_when_it_was_queued()
    {
        var voice = Start();
        var formatter = new GodModeAnnouncementFormatter(Phrases, voice.Conversation, voice.Board, voice.Tools.Names);
        var item = Question(Mic.ProjectId, "Issue #283: Voice: mic timeout, on headsets", "Skal timeouten være 30 sekunder?") with { Profile = "Mega", Root = "GodMode" };
        voice.Servers.PushAttention(Server, item);
        var queued = voice.Board.AnnouncementOf(voice.Board.ItemOf(Mic)!, "issue 283, mic timeout i GodMode, profil Mega har et spørgsmål");

        await voice.Tools.ProjectStatusAsync("283", CancellationToken.None);
        Assert.Equal("issue 283 har et spørgsmål.", formatter.Format([queued], Danish));

        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);
        Assert.Equal("issue 283, mic timeout, har et spørgsmål.", formatter.Format([queued], Danish));
    }

    [Fact]
    public void An_announcement_with_nothing_said_before_it_gets_the_full_anchor()
    {
        var voice = Start();
        var formatter = new GodModeAnnouncementFormatter(Phrases, voice.Conversation, voice.Board, voice.Tools.Names);
        voice.Servers.PushAttention(Server, Finished(Master.ProjectId, "master", "Pushet.") with { Profile = "Private", Root = "voicebot" });
        var queued = voice.Board.AnnouncementOf(voice.Board.ItemOf(Master)!, "branch master er færdig");

        Assert.Equal("branch master i voicebot, profil Private, er færdig.", formatter.Format([queued], Danish));
    }

    /// <summary>A line about two projects names each by what came before: "Til issue 283, mic timeout, eller …".</summary>
    [Fact]
    public async Task The_which_question_after_an_announced_switch_names_both_with_their_topics()
    {
        var voice = Start();
        voice.Servers.SetStatus(Server, (await voice.Servers.GetStatusAsync(Mic, CancellationToken.None)) with { State = ProjectState.WaitingInput });
        await voice.Tools.ProjectStatusAsync("283", CancellationToken.None);
        var formatter = new GodModeAnnouncementFormatter(Phrases, voice.Conversation, voice.Board, voice.Tools.Names);
        formatter.Format([new Announcement("issue 101 har et spørgsmål", Columns.Key)], Danish);
        voice.Conversation.SpeechEnded();

        var result = await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal("Til issue 283, mic timeout, eller issue 101, Drop the old columns?", voice.Conversation.TakeSaid(result));
    }
}
