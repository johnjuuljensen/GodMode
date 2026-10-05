using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// An announcement never silently changes where an answer goes (#461): an answer that names no project, within
/// <see cref="VoiceConversation.AnnouncedSwitchWindow"/> of an announcement that changed the project talked about, sends
/// nothing, and voice asks which of the two it is for, naming both.
/// </summary>
public sealed class AnnouncedSwitchTests
{
    private const string ServerA = "server-a";
    private const string ServerB = "server-b";
    private static readonly SessionLanguages Danish = new("da-DK");
    private static readonly ProjectRef P101 = new(ServerA, "p/r/101");
    private static readonly ProjectRef P283 = new(ServerB, "p/r/283");

    private sealed record Voice(FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation,
        GodModeAnnouncementFormatter Formatter, ManualTime Time);

    /// <summary>101 and 283 both wait on a question, and the user is talking about 283.</summary>
    private static async Task<Voice> TalkingAbout283Async()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var time = new ManualTime();
        var conversation = new VoiceConversation(time);
        var phrases = new VoicePhrases(Danish);
        var tools = new VoiceTools(servers, board, projects, handles, conversation, phrases: phrases);
        servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Skal jeg slette kolonnerne?"));
        servers.Set(ServerB, Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?"));

        await tools.ProjectStatusAsync("283", CancellationToken.None);
        Assert.Equal(P283, conversation.Current);
        return new Voice(servers, tools, conversation, new GodModeAnnouncementFormatter(phrases, conversation, board), time);
    }

    /// <summary>The project's announcement is said, as the session says it: formatted, then spoken to its end.</summary>
    private static void Announce(Voice voice, ProjectRef project, string text)
    {
        voice.Formatter.Format([new Announcement(text, project.Key)], Danish);
        voice.Conversation.SpeechEnded();
    }

    [Fact]
    public async Task An_unnamed_answer_just_after_an_announcement_of_another_project_asks_which_and_sends_nothing()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");
        voice.Time.Advance(TimeSpan.FromSeconds(2));

        var result = await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Empty(voice.Servers.Replies);
        Assert.Contains("Nothing was sent", result);
        Assert.Equal("Til issue 283, voice, eller issue 101, cleanup?", voice.Conversation.TakeSaid(result));
    }

    [Fact]
    public async Task The_answer_then_goes_where_the_user_chose()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");
        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        await voice.Tools.AnswerAsync("283", "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    [Fact]
    public async Task Outside_the_window_an_unnamed_answer_goes_to_the_announced_project()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");
        voice.Time.Advance(VoiceConversation.AnnouncedSwitchWindow + TimeSpan.FromSeconds(1));

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P101, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>The window runs from the end of the announcement's speech: one still being said holds an answer however long it takes.</summary>
    [Fact]
    public async Task An_unnamed_answer_while_the_announcement_is_still_said_asks()
    {
        var voice = await TalkingAbout283Async();
        voice.Formatter.Format([new Announcement("issue 101 spørger: Skal jeg slette kolonnerne?", P101.Key)], Danish);
        voice.Time.Advance(VoiceConversation.AnnouncedSwitchWindow * 2);

        var result = await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Empty(voice.Servers.Replies);
        Assert.Equal("Til issue 283, voice, eller issue 101, cleanup?", voice.Conversation.TakeSaid(result));
    }

    [Fact]
    public async Task An_announcement_of_the_project_already_talked_about_asks_nothing()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P283, "issue 283 har et spørgsmål");

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>The user chose the announced project themselves, by asking about it: their unnamed answer goes there.</summary>
    [Fact]
    public async Task A_tool_naming_a_project_after_the_announcement_ends_the_question()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P101, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>
    /// "Hvad spørger den om?" right after the announcement asks about the announced project: only an answer asks which,
    /// and the status, naming 101, is the user's choice.
    /// </summary>
    [Fact]
    public async Task An_unnamed_status_ask_just_after_an_announcement_reads_the_announced_project_and_asks_nothing()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");

        var result = await voice.Tools.ProjectStatusAsync(null, CancellationToken.None);

        Assert.Contains("Skal jeg slette kolonnerne?", result);
        Assert.DoesNotContain("Nothing was sent", result);
        Assert.Equal(P101, voice.Conversation.Current);
        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);
        Assert.Equal(P101, Assert.Single(voice.Servers.Replies).Project);
    }

    [Fact]
    public async Task A_named_answer_just_after_an_announcement_goes_where_it_names()
    {
        var voice = await TalkingAbout283Async();
        Announce(voice, P101, "issue 101 har et spørgsmål");

        await voice.Tools.AnswerAsync("283", "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>The issue's race, through a whole session: "ja" meant for 283, with 101's announcement said in between.</summary>
    [Fact]
    public async Task In_a_session_the_system_asks_which_and_the_users_choice_is_sent()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            // Both said by the code: the model is not called after them (#456)
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja." })
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja.", [VoiceTools.ProjectParameter] = "283" }).Respond("Sendt til issue 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerB, Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?", minutesAgo: 30)); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");
        voice.Transcriptions.AddFinal("hvad spørger 283 om");
        await voice.Events.SaidAsync("issue 283 spørger: Skal jeg bruge den eksisterende migration?");

        var spoken = voice.Events.States.Count(s => s == VoiceState.Speaking);
        servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Skal jeg slette kolonnerne?"));
        await voice.Events.SaidAsync("issue 101, cleanup, har et spørgsmål.");
        // Said to its end, so the answer comes in the window after it rather than over it
        await SpeechEndedAsync(voice, spoken);
        voice.Transcriptions.AddFinal("svar at ja");
        await voice.Events.SaidAsync("Til issue 283, voice, eller issue 101, cleanup?");
        Assert.Empty(servers.Replies);

        voice.Transcriptions.AddFinal("283");
        await voice.Events.SaidAsync("Sendt til issue 283, voice.");
        Assert.Equal(P283, Assert.Single(servers.Replies).Project);
    }

    /// <summary>The session spoke once more than <paramref name="spoken"/> times, and listens again.</summary>
    internal static Task SpeechEndedAsync(OfflineVoice voice, int spoken) =>
        Eventually.UntilAsync(() => voice.Events.States.Count(s => s == VoiceState.Speaking) > spoken && voice.Events.States.Last() == VoiceState.Listening,
            () => $"the speech to end; states: {string.Join(", ", voice.Events.States)}");
}
