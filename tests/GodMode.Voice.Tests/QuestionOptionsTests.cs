using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// A pending AskUserQuestion's options reach voice (#529): <c>project_status</c> and the announcement say them, and an
/// answer that picks one answers the question with it, as the inbox does. As in the issue's log (14:25): a chat asked
/// "File issues for these findings?" with three options, and voice said only the question.
/// </summary>
public sealed class QuestionOptionsTests
{
    private const string Server = "server-a";
    private const string Id = "Default/root/260930-chat-findings-a1";
    private const string Asked = "File issues for these findings?";

    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));
    private static readonly VoicePhrases English = new(new SessionLanguages("en-US"));

    private static readonly PendingQuestion Pending = new("req-7",
        [new QuestionItem(Asked, "Issues", [new QuestionOption("Yes, file them", "One issue each"), new QuestionOption("Not now", null),
            new QuestionOption("Only the first", null)], MultiSelect: false)], DateTime.UtcNow);

    private static AttentionItem Item(PendingQuestion? pending = null) =>
        Question(Id, "findings", Asked) with { Question = pending ?? Pending };

    private static (FakeServers Servers, VoiceTools Tools) Asking(PendingQuestion? pending = null)
    {
        var servers = new FakeServers(Server);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation(), phrases: Danish);
        servers.Set(Server, Item(pending));
        return (servers, tools);
    }

    [Fact]
    public async Task Project_status_gives_the_options_and_says_them()
    {
        var (_, tools) = Asking();

        var result = await tools.ProjectStatusAsync("findings", CancellationToken.None);

        Assert.Contains("Options: 1. \"Yes, file them\"; 2. \"Not now\"; 3. \"Only the first\".", result);
        Assert.Equal($"findings spørger: {Asked} Valg: Yes, file them; Not now; eller Only the first.",
            tools.Conversation.TakeSaid(result));
    }

    [Fact]
    public void The_announcement_reads_the_question_and_its_options()
    {
        var name = new SpokenName("findings");

        Assert.Equal($"findings spørger: {Asked} Valg: Yes, file them; Not now; eller Only the first", Danish.Announce(name, Item()));
        Assert.Equal($"findings asks: {Asked} Options: Yes, file them; Not now; or Only the first", English.Announce(name, Item()));
        // In a list of several, the options are left to the status
        Assert.Equal("findings har et spørgsmål", Danish.Announce(name, Item(), choices: false));
    }

    [Fact]
    public void A_question_with_no_options_is_announced_as_before()
    {
        Assert.Equal("findings har et spørgsmål", Danish.Announce(new SpokenName("findings"), Question(Id, "findings", Asked)));
    }

    [Theory]
    [InlineData("2", "Not now")]
    [InlineData("to", "Not now")]
    [InlineData("den anden", "Not now")]
    [InlineData("the second one", "Not now")]
    [InlineData("option 3", "Only the first")]
    [InlineData("den sidste", "Only the first")]
    [InlineData("Not now.", "Not now")]
    [InlineData("yes", "Yes, file them")]
    public async Task An_answer_that_picks_an_option_answers_the_question_with_it(string option, string label)
    {
        var (servers, tools) = Asking();

        var result = await tools.AnswerAsync("findings", "", CancellationToken.None, option);

        Assert.StartsWith($"Sent to findings: \"{label}\".", result);
        var answer = Assert.Single(servers.Answers);
        Assert.Equal("req-7", answer.RequestId);
        Assert.Equal(label, answer.Answers[Asked]);
        Assert.Empty(servers.Replies);
    }

    [Fact]
    public async Task An_answer_that_is_an_option_picks_it_and_any_other_goes_as_the_users_words()
    {
        var (servers, tools) = Asking();

        await tools.AnswerAsync("findings", "Not now", CancellationToken.None);
        await tools.AnswerAsync("findings", "File the first two, and drop the rest", CancellationToken.None);

        Assert.Equal("Not now", Assert.Single(servers.Answers).Answers[Asked]);
        Assert.Equal("File the first two, and drop the rest", Assert.Single(servers.Replies).Text);
    }

    [Fact]
    public async Task An_option_that_is_none_of_them_sends_nothing_and_lists_them()
    {
        var (servers, tools) = Asking();

        var result = await tools.AnswerAsync("findings", "", CancellationToken.None, "5");

        Assert.Contains("'5' is none of findings's options (1. \"Yes, file them\"; 2. \"Not now\"; 3. \"Only the first\")", result);
        Assert.Empty(servers.Answers);
        Assert.Empty(servers.Replies);
    }

    [Fact]
    public async Task Several_options_of_a_multi_select_are_joined_as_the_hub_takes_them()
    {
        var pending = Pending with { Questions = [Pending.Questions[0] with { MultiSelect = true }] };
        var (servers, tools) = Asking(pending);

        await tools.AnswerAsync("findings", "", CancellationToken.None, "1 og 3");

        Assert.Equal("Yes, file them, Only the first", Assert.Single(servers.Answers).Answers[Asked]);
    }
}
