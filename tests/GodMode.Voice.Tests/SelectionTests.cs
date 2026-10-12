using VoiceBot.Core.Resources;
using VoiceBot.Testing;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice knows which project the app shows (#559, <see cref="VoiceConversation.Selected"/>): a request that names no
/// project goes to it when no other was talked about since, "den her" / "this one" is it, and the model is told it. With
/// none selected, voice behaves as before.
/// </summary>
public sealed class SelectionTests
{
    private const string ServerA = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");
    private static readonly ProjectRef P101 = new(ServerA, "p/r/101");
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283");

    private sealed record Voice(FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation);

    /// <summary>101 and 283 both wait on a question; nothing was talked about yet.</summary>
    private static Voice Start()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation(new ManualTime());
        var tools = new VoiceTools(servers, board, projects, handles, conversation, phrases: new VoicePhrases(Danish));
        servers.Set(ServerA,
            Question("p/r/101", "101-cleanup", "Skal jeg slette kolonnerne?"),
            Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?"));
        return new Voice(servers, tools, conversation);
    }

    [Fact]
    public async Task An_unnamed_answer_goes_to_the_selected_project()
    {
        var voice = Start();
        voice.Conversation.Select(P283);

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    [Fact]
    public async Task With_none_selected_an_unnamed_answer_asks_which_as_before()
    {
        var voice = Start();

        var result = await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Empty(voice.Servers.Replies);
        Assert.Contains("No project is being talked about", result);
    }

    [Theory]
    [InlineData("den her")]
    [InlineData("Det her.")]
    [InlineData("this one")]
    [InlineData("the open one")]
    public async Task This_one_is_the_selected_project_whatever_was_talked_about(string said)
    {
        var voice = Start();
        voice.Conversation.Select(P283);
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);

        await voice.Tools.AnswerAsync(said, "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    [Fact]
    public async Task With_none_selected_this_one_is_the_project_talked_about()
    {
        var voice = Start();
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);

        await voice.Tools.AnswerAsync("den her", "Ja.", CancellationToken.None);

        Assert.Equal(P101, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>The user turned to it on screen after the one voice talked about: it is what they mean now.</summary>
    [Fact]
    public async Task A_new_selection_wins_over_the_project_talked_about_before_it()
    {
        var voice = Start();
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);
        voice.Conversation.Select(P283);

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    /// <summary>Talking about another project by voice moves the conversation, and leaves the app's selection as it was.</summary>
    [Fact]
    public async Task A_project_talked_about_after_the_selection_takes_the_unnamed_answer()
    {
        var voice = Start();
        voice.Conversation.Select(P283);
        await voice.Tools.ProjectStatusAsync("101", CancellationToken.None);
        // The page reports the same selection again (a reload, another window's focus and back): it changes nothing
        voice.Conversation.Select(P283);

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Equal(P101, Assert.Single(voice.Servers.Replies).Project);
        Assert.Equal(P283, voice.Conversation.Selected);
    }

    [Fact]
    public async Task A_closed_selection_leaves_the_conversation_where_it_was()
    {
        var voice = Start();
        voice.Conversation.Select(P283);
        voice.Conversation.Select(null);

        await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Null(voice.Conversation.Selected);
        Assert.Equal(P283, Assert.Single(voice.Servers.Replies).Project);
    }

    [Fact]
    public async Task A_selected_project_deleted_since_is_none()
    {
        var voice = Start();
        voice.Conversation.Select(P283);
        voice.Servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Skal jeg slette kolonnerne?"));
        voice.Servers.DeleteProject(ServerA, "p/r/283");

        var result = await voice.Tools.AnswerAsync(null, "Ja.", CancellationToken.None);

        Assert.Empty(voice.Servers.Replies);
        Assert.Contains("No project is being talked about", result);
    }

    [Fact]
    public async Task The_model_is_told_which_project_is_on_screen()
    {
        var model = new ScriptedChatClient().Respond("Klar.").Respond("Klar.");
        var servers = new FakeServers(ServerA);
        servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.ListeningAsync();

        voice.Session.Select(P283);
        voice.Transcriptions.AddFinal("Hvad sker der?");
        await Eventually.UntilAsync(() => model.Requests.Count >= 1, () => "the model to be asked");

        var prompt = model.Requests.Last()[0].Text;
        Assert.Contains("ON SCREEN: issue 283", prompt);
    }

    [Fact]
    public async Task With_none_selected_the_model_is_told_nothing_on_screen()
    {
        var model = new ScriptedChatClient().Respond("Klar.").Respond("Klar.");
        var servers = new FakeServers(ServerA);
        servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.ListeningAsync();

        voice.Transcriptions.AddFinal("Hvad sker der?");
        await Eventually.UntilAsync(() => model.Requests.Count >= 1, () => "the model to be asked");

        Assert.DoesNotContain("ON SCREEN:", model.Requests.Last()[0].Text);
    }
}
