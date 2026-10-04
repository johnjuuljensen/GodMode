using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Announcements are checked again when they are about to be said (issue #462): one whose item no longer needs the user
/// is dropped, and those still waiting are said most urgent first.
/// </summary>
public sealed class AnnouncementCheckTests
{
    private const string ServerA = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");
    private static readonly ProjectRef P101 = new(ServerA, "p/r/101-cleanup");
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283-voice");

    private static (FakeServers Servers, AttentionBoard Board, VoiceConversation Conversation, GodModeAnnouncementFormatter Formatter) Voice()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var board = new AttentionBoard(servers, handles, new ProjectBoard(servers, handles));
        var conversation = new VoiceConversation();
        return (servers, board, conversation, new GodModeAnnouncementFormatter(new VoicePhrases(Danish), conversation, board));
    }

    private static Announcement Of(AttentionBoard board, ProjectRef project, string text) =>
        board.AnnouncementOf(board.ItemOf(project) ?? throw new InvalidOperationException($"no item of {project}"), text);

    [Fact]
    public async Task An_item_cleared_on_the_server_while_the_user_speaks_is_never_said()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());
        await voice.Events.SaidAsync("Klar.");

        // The user is speaking: announcements wait for the pause after their words
        voice.Transcriptions.AddPartial("vent lige jeg skal");
        await Eventually.UntilAsync(() => !voice.Events.Partials.IsEmpty, () => "the partial to be heard");
        servers.Set(ServerA, Question(P101.ProjectId, "101-cleanup", "Skal jeg slette de gamle kolonner?"));
        // Answered on screen before the pause
        servers.Set(ServerA);
        // Something else comes, and then the pause
        servers.Set(ServerA, Question(P283.ProjectId, "283-voice", "Ny migration?"));

        await Eventually.UntilAsync(() => voice.Events.Responses.Any(r => r.Contains("283")),
            () => $"the bot to announce 283; it said: {string.Join(" | ", voice.Events.Responses)}");
        Assert.Equal(["Klar.", "issue 283, voice, har et spørgsmål."], voice.Events.Responses);
    }

    [Fact]
    public async Task A_finished_and_a_permission_queued_together_say_the_permission_first()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient(), connect: _ =>
        {
            servers.Set(ServerA,
                Finished(P101.ProjectId, "101-cleanup", "Done.", minutesAgo: 30),
                Permission(P283.ProjectId, "283-voice", "Bash: git push"));
            return Task.CompletedTask;
        });

        await voice.Events.SaidAsync("2 venter på dig: issue 283, voice, skal have tilladelse: Bash: git push. Svar på skærmen. issue 101, cleanup, er færdig.");
    }

    [Fact]
    public void Several_are_said_permission_and_question_before_error_before_finished()
    {
        var (servers, board, _, formatter) = Voice();
        servers.Set(ServerA,
            Finished("p/r/1", "1-a", "Done.", minutesAgo: 40),
            Question("p/r/2", "2-b", "No branch.", minutesAgo: 30) with { Kind = AttentionKind.Error },
            Question("p/r/3", "3-c", "Hvilken?", minutesAgo: 20),
            Permission("p/r/4", "4-d", "Bash: ls", minutesAgo: 10));

        var said = formatter.Format([
            new Announcement("en er oprettet"),
            Of(board, new(ServerA, "p/r/1"), "1 er færdig"),
            Of(board, new(ServerA, "p/r/2"), "2 fejlede"),
            Of(board, new(ServerA, "p/r/3"), "3 har et spørgsmål"),
            Of(board, new(ServerA, "p/r/4"), "4 skal have tilladelse"),
        ], Danish);

        Assert.Equal("5 venter på dig: 3 har et spørgsmål. 4 skal have tilladelse. 2 fejlede. 1 er færdig. en er oprettet.", said);
    }

    [Fact]
    public void An_important_projects_result_is_said_before_a_normal_projects_question()
    {
        var (servers, board, _, formatter) = Voice();
        servers.Set(ServerA,
            Question(P101.ProjectId, "101-cleanup", "Hvilken?"),
            Finished(P283.ProjectId, "283-voice", "Done.") with { Importance = Importance.Important, Alert = AttentionAlert.Interrupt });

        var said = formatter.Format([Of(board, P101, "101 har et spørgsmål"), Of(board, P283, "283 er færdig")], Danish);

        Assert.Equal("2 venter på dig: 283 er færdig. 101 har et spørgsmål.", said);
    }

    [Fact]
    public void An_item_marked_seen_is_dropped_and_leaves_the_project_talked_about_as_it_was()
    {
        var (servers, board, conversation, formatter) = Voice();
        servers.Set(ServerA, Finished(P101.ProjectId, "101-cleanup", "Done."));
        var announcement = Of(board, P101, "101 er færdig");
        conversation.Current = P283;

        servers.Set(ServerA);   // marked seen: the server's list no longer has it

        Assert.Equal("", formatter.Format([announcement], Danish));
        Assert.Equal(P283, conversation.Current);
        Assert.Null(conversation.TakeAnnouncedSwitch());
    }

    [Fact]
    public void Of_two_one_dropped_the_other_is_said_alone_and_is_what_the_conversation_is_about()
    {
        var (servers, board, conversation, formatter) = Voice();
        var question = Question(P283.ProjectId, "283-voice", "Ny?");
        servers.Set(ServerA, Question(P101.ProjectId, "101-cleanup", "Hvilken?"), question);
        var first = Of(board, P101, "101 har et spørgsmål");
        var second = Of(board, P283, "283 har et spørgsmål");

        servers.Set(ServerA, question);   // 101 answered on screen

        Assert.Equal("283 har et spørgsmål.", formatter.Format([first, second], Danish));
        Assert.Equal(P283, conversation.Current);
    }

    [Fact]
    public void A_session_that_moved_on_drops_its_old_item_but_not_its_new_one()
    {
        var (servers, board, _, formatter) = Voice();
        servers.Set(ServerA, Finished(P101.ProjectId, "101-cleanup", "Done.", minutesAgo: 10));
        var old = Of(board, P101, "101 er færdig");
        servers.Set(ServerA, Question(P101.ProjectId, "101-cleanup", "Hvilken?", minutesAgo: 1));
        var now = Of(board, P101, "101 har et spørgsmål");

        Assert.Equal("101 har et spørgsmål.", formatter.Format([old, now], Danish));
    }

    [Fact]
    public void An_item_sent_to_the_inbox_alone_before_the_pause_is_not_said()
    {
        var (servers, board, _, formatter) = Voice();
        var item = Finished(P101.ProjectId, "101-cleanup", "Done.");
        servers.Set(ServerA, item);
        var announcement = Of(board, P101, "101 er færdig");

        servers.Set(ServerA, item with { Importance = Importance.Quiet, Alert = AttentionAlert.Inbox });

        Assert.Equal("", formatter.Format([announcement], Danish));
    }

    [Fact]
    public void The_same_item_pushed_again_with_new_text_is_still_said()
    {
        var (servers, board, _, formatter) = Voice();
        var item = Question(P101.ProjectId, "101-cleanup", "Hvilken?");
        servers.Set(ServerA, item);
        var announcement = Of(board, P101, "101 har et spørgsmål");

        servers.Set(ServerA, item with { Text = "Hvilken branch?" });

        Assert.Equal("101 har et spørgsmål.", formatter.Format([announcement], Danish));
    }
}
