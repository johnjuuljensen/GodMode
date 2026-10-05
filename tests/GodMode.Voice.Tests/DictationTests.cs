using System.Text.Json;
using System.Threading.Channels;
using GodMode.ClientBase.Hub;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Dictation (#459): "Diktér til 283", then the user speaks freely, across pauses, and the text is sent as said, never
/// rephrased by the model, on "send"; "annullér" or "stop" drops it. Each final is what the transcriber commits after a
/// pause longer than its threshold, so three finals are three parts said with two pauses between them.
/// </summary>
public sealed class DictationTests
{
    private const string ServerA = "server-a";
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283");
    private static readonly SessionLanguages Danish = new("da-DK");
    private const string Started = "Diktat til issue 283. Sig send, eller annullér.";

    private const string First = "Brug den eksisterende migration, ikke en ny.";
    private const string Second = "Og når testene er grønne, så skriv en kort note og send den til Peter.";
    private const string Third = "Hvad venter? Det skal du ikke svare på, det er en del af beskeden.";

    /// <summary>283 asks a question, announced; a model that is never to be called while dictating.</summary>
    private static async Task<(FakeServers Servers, OfflineVoice Voice)> AskedAsync(ScriptedChatClient? model = null)
    {
        var servers = new FakeServers();
        var voice = await OfflineVoice.StartAsync(servers, model ?? new ScriptedChatClient(),
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");
        return (servers, voice);
    }

    /// <summary>An utterance as ElevenLabs sends it: partials growing word by word, then the final, committed on a pause.</summary>
    private static void Say(OfflineVoice voice, string text)
    {
        var words = text.Split(' ');
        for (var count = 1; count <= words.Length; count++)
            voice.Transcriptions.AddPartial(string.Join(" ", words.Take(count)));
        voice.Transcriptions.AddFinal(text);
    }

    private static Task SaidStartingAsync(OfflineVoice voice, string start) =>
        Eventually.UntilAsync(() => voice.Events.Responses.Any(r => r.StartsWith(start, StringComparison.Ordinal)),
            () => $"the bot to say \"{start}…\"; it said: {string.Join(" | ", voice.Events.Responses)}");

    [Fact]
    public async Task Dictation_across_two_pauses_then_send_is_one_answer_with_all_three_parts_word_for_word()
    {
        var model = new ScriptedChatClient();
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        Say(voice, "Diktér til 283.");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, Second);
        Say(voice, Third);
        Say(voice, "Send.");
        await voice.Events.SaidAsync("Sender 4 sætninger til issue 283, der starter: Brug den eksisterende migration, ikke en ny …");

        Assert.Equal((P283, $"{First} {Second} {Third}"), Assert.Single(servers.Replies));
        // The model never heard a word of it: not the start, the parts, "Hvad venter?" in them, nor "send"
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Annuller_drops_the_dictation_and_nothing_is_sent()
    {
        var model = new ScriptedChatClient();
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Annullér.");
        await voice.Events.SaidAsync("Annulleret. Intet sendt til issue 283.");

        Assert.Empty(servers.Replies);
        Assert.Equal(0, model.Calls);
    }

    /// <summary>"Stop" while dictating is the safe reading: the dictation is dropped, nothing sent.</summary>
    [Fact]
    public async Task Stop_drops_the_dictation_and_nothing_is_sent()
    {
        var (servers, voice) = await AskedAsync();
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Stop.");
        await voice.Events.SaidAsync("Annulleret. Intet sendt til issue 283.");

        Assert.Empty(servers.Replies);
    }

    /// <summary>After a dictation, the next words are the chat's again.</summary>
    [Fact]
    public async Task After_send_the_next_words_go_to_the_chat()
    {
        var model = new ScriptedChatClient().Respond("Klar.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Send");
        await SaidStartingAsync(voice, "Sender 1 sætning til issue 283");
        Say(voice, "Hvad venter?");
        await Eventually.UntilAsync(() => model.Calls == 1, () => "the chat to hear what came after");

        Assert.Contains("Hvad venter?", Assert.Single(model.UserTexts));
        Assert.Single(servers.Replies);
    }

    /// <summary>The terminator as the last sentence of a final, after a sentence break, ends it, and the words before it are sent.</summary>
    [Fact]
    public async Task Send_as_its_own_sentence_at_the_end_of_a_final_sends()
    {
        var (servers, voice) = await AskedAsync();
        await using var _ = voice;

        Say(voice, "Diktér til 283. Brug den eksisterende migration.");
        await voice.Events.SaidAsync(Started);
        Say(voice, "Det var det. Send.");
        await SaidStartingAsync(voice, "Sender 2 sætninger til issue 283");

        Assert.Equal((P283, "Brug den eksisterende migration. Det var det."), Assert.Single(servers.Replies));
    }

    [Fact]
    public async Task A_project_that_is_working_is_refused_up_front()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.AddProject(ServerA, "p/r/283", "283-voice", state: ProjectState.Running); return Task.CompletedTask; });
        await voice.Events.SaidAsync("Klar.");

        Say(voice, "Diktér til 283");
        await SaidStartingAsync(voice, "issue 283");
        Assert.EndsWith("arbejder. Diktér, når den venter på dig.", voice.Events.Responses.Last());

        // Nothing is dictated: "send" is the chat's
        Say(voice, "Send");
        await Eventually.UntilAsync(() => model.Calls == 1, () => "the chat to hear it");
        Assert.Empty(servers.Replies);
    }

    [Fact]
    public async Task A_project_waiting_on_a_permission_is_refused_up_front()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Permission("p/r/283", "283-voice", "git push")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, skal have tilladelse: git push. Svar på skærmen.");

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync("issue 283 skal have tilladelse: git push. Svar på skærmen.");

        Say(voice, "Send");
        await Eventually.UntilAsync(() => model.Calls == 1, () => "the chat to hear it");
        Assert.Empty(servers.Replies);
    }

    [Fact]
    public async Task A_project_no_handle_names_starts_no_dictation()
    {
        var (_, voice) = await AskedAsync();
        await using var __ = voice;

        Say(voice, "Diktér til 999");
        await voice.Events.SaidAsync("Ukendt projekt: 999. Intet diktat.");
    }

    // The rules, on their own

    [Theory]
    [InlineData("Send.", Dictation.Terminator.Send, "")]
    [InlineData("send den", Dictation.Terminator.Send, "")]
    [InlineData("Det var det. Send.", Dictation.Terminator.Send, "Det var det.")]
    [InlineData("Annullér.", Dictation.Terminator.Cancel, "")]
    [InlineData("Stop", Dictation.Terminator.Cancel, "")]
    [InlineData("og send den til Peter", null, "og send den til Peter")]
    [InlineData("Skriv en note og send.", null, "Skriv en note og send.")]
    [InlineData("Send en besked til Peter.", null, "Send en besked til Peter.")]
    [InlineData("Stop ikke før testene er grønne.", null, "Stop ikke før testene er grønne.")]
    public void A_terminator_counts_only_as_a_sentence_on_its_own(string final, Dictation.Terminator? terminator, string before) =>
        Assert.Equal((terminator, before), Dictation.Ends(final));

    [Theory]
    [InlineData("Diktér til 283", "283", "")]
    [InlineData("Dikter til issue 283.", "issue 283", "")]
    [InlineData("Dictate to branch master. Use the migration.", "branch master", "Use the migration.")]
    public void A_start_names_the_project_and_what_follows(string final, string reference, string after) =>
        Assert.Equal((reference, after), Dictation.Starts(final));

    [Theory]
    [InlineData("Brug den eksisterende migration, ikke en ny. Og så videre.", "Brug den eksisterende migration, ikke en ny …")]
    [InlineData("Kør testene igen nu og fortæl mig hvad der fejler.", "Kør testene igen nu og fortæl mig hvad …")]
    [InlineData("Push den.", "Push den")]
    public void The_read_back_says_how_it_starts(string text, string opening) => Assert.Equal(opening, Dictation.Opening(text));

    [Theory]
    [InlineData("Diktér")]
    [InlineData("Hvad diktér til 283")]
    [InlineData("Svar 283 at den skal diktere til Peter")]
    public void Other_words_start_none(string final) => Assert.Null(Dictation.Starts(final));

    /// <summary>A dictation, on its own, with a fake clock.</summary>
    private static async Task<(FakeServers Servers, Dictation Dictation, ManualTime Time)> DictatingAsync()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        _ = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation();
        var time = new ManualTime();
        var dictation = new Dictation(servers, handles, new ProjectNames(projects, handles, conversation), conversation, new VoicePhrases(Danish), time);
        servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?"));
        Assert.NotNull(await dictation.StartAsync("Diktér til 283", CancellationToken.None));
        Assert.True(dictation.Active);
        Assert.Null(await dictation.HearAsync(First, CancellationToken.None));
        return (servers, dictation, time);
    }

    /// <summary>The mic closed, or voice stopped, with no terminator: dropped, and a later "send" sends nothing.</summary>
    [Fact]
    public async Task A_dictation_abandoned_is_dropped_and_never_sent()
    {
        var (servers, dictation, _) = await DictatingAsync();

        dictation.Abandon();

        Assert.False(dictation.Active);
        Assert.Null(await dictation.HearAsync("Send", CancellationToken.None));
        Assert.Empty(servers.Replies);
    }

    [Fact]
    public async Task A_dictation_with_nothing_heard_for_its_window_is_dropped_and_said_so()
    {
        var (servers, dictation, time) = await DictatingAsync();

        time.Advance(dictation.IdleWindow - TimeSpan.FromSeconds(1));
        Assert.Null(dictation.Tick());
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.StartsWith("Diktatet til issue 283", dictation.Tick());
        Assert.False(dictation.Active);
        Assert.Empty(servers.Replies);
    }

    /// <summary>A permission asked for while dictating holds the send: nothing goes out, and the dictation is kept.</summary>
    [Fact]
    public async Task A_send_to_a_project_that_now_waits_on_a_permission_is_held_and_kept()
    {
        var (servers, dictation, _) = await DictatingAsync();
        servers.Set(ServerA, Permission("p/r/283", "283-voice", "git push"));
        servers.SetStatus(ServerA, (await servers.GetStatusAsync(P283, CancellationToken.None)) with
        {
            State = ProjectState.WaitingPermission,
            PendingPermission = new PendingPermission("req-1", "Bash", "git push", DateTime.UtcNow),
        });

        var said = await dictation.HearAsync("Send", CancellationToken.None);

        Assert.StartsWith("Intet sendt.", said);
        Assert.True(dictation.Active);
        Assert.Equal([First], dictation.Parts);
        Assert.Empty(servers.Replies);
    }

    /// <summary>
    /// Announcements wait while dictating: one in a pause to think would break the user's train of thought. They are
    /// said once it is sent or dropped.
    /// </summary>
    [Fact]
    public async Task Announcements_wait_while_dictating()
    {
        var (_, dictation, _) = await DictatingAsync();
        var channel = Channel.CreateUnbounded<Announcement>();
        var announcements = new HeldAnnouncements(channel.Writer, new SessionCreates(new FakeServers(), new ProjectHandles()), dictation);

        announcements.Write(new Announcement("issue 101 har et spørgsmål"));
        Assert.False(channel.Reader.TryRead(out _));

        await dictation.HearAsync("Send", CancellationToken.None);

        Assert.True(channel.Reader.TryRead(out var said));
        Assert.Equal("issue 101 har et spørgsmål", said.Text);
    }

    /// <summary>
    /// Against the real server: the dictation reaches FakeClaude's stdin through ReplyByVoice, word for word, marked as
    /// transcribed speech (#460), as one answer.
    /// </summary>
    [Fact]
    public async Task A_dictation_reaches_the_session_word_for_word_marked_as_spoken()
    {
        await using var server = await TestServer.StartAsync(EndToEndTests.Asking("Hvad skal jeg gøre?"));
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var asking = await EndToEndTests.CreateAsync(hub, "283-add-migration");
        await EndToEndTests.WaitForAttentionAsync(hub, asking.Id);

        var model = new ScriptedChatClient();
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("issue 283, add migration, har et spørgsmål.");

        Say(voice, "Diktér til 283");
        await SaidStartingAsync(voice, "Diktat til issue 283");
        Say(voice, First);
        Say(voice, Second);
        Say(voice, "Send");
        await SaidStartingAsync(voice, "Sender 2 sætninger til issue 283");

        var dictated = $"{First} {Second}";
        await Eventually.UntilAsync(() => server.StdinOf(asking.Id).Count == 2, () => $"the dictation on stdin: {string.Join(" | ", server.StdinOf(asking.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(SpokenInput.Mark(dictated)), server.StdinOf(asking.Id)[1]);
        Assert.Equal(0, model.Calls);
    }
}
