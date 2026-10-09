using System.Text.Json;
using System.Threading.Channels;
using GodMode.ClientBase.Hub;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;
using VoiceBot.Core.Pipeline;
using VoiceBot.Providers.ElevenLabs;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Dictation (#459): "Diktér til 283", then the user speaks freely, across pauses, and the text is sent as said, never
/// rephrased by the model, on "diktat slut"; "annullér diktat" drops it (#520: a bare "send" or "stop" is dictated). Each final is what the transcriber commits after a
/// pause longer than its threshold, so three finals are three parts said with two pauses between them.
/// </summary>
public sealed class DictationTests
{
    private const string ServerA = "server-a";
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283");
    private static readonly SessionLanguages Danish = new("da-DK");
    private const string Started = "Diktat til issue 283. Sig diktat slut, eller annullér diktat.";

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
    public async Task Dictation_across_two_pauses_then_diktat_slut_is_one_answer_with_all_three_parts_word_for_word()
    {
        var model = new ScriptedChatClient();
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        Say(voice, "Diktér til 283.");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, Second);
        Say(voice, Third);
        Say(voice, "Diktat slut.");
        await voice.Events.SaidAsync("Sender 4 sætninger til issue 283, der starter: Brug den eksisterende migration, ikke en ny …");

        Assert.Equal((P283, $"{First} {Second} {Third}"), Assert.Single(servers.Replies));
        // The model never heard a word of it: not the start, the parts, "Hvad venter?" in them, nor "diktat slut"
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Annuller_diktat_drops_the_dictation_and_nothing_is_sent()
    {
        var model = new ScriptedChatClient();
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Annullér diktat.");
        await voice.Events.SaidAsync("Annulleret. Intet sendt til issue 283.");

        Assert.Empty(servers.Replies);
        Assert.Equal(0, model.Calls);
    }

    /// <summary>
    /// A bare "send", "stop" or "annullér" is dictated like any other word (#520): "send" is too likely to be said in the
    /// text, and a "stop" that dropped it lost the user's words. Only the explicit phrase ends it.
    /// </summary>
    [Fact]
    public async Task Bare_send_stop_and_annuller_are_dictated_and_diktat_slut_sends_them_all()
    {
        var (servers, voice) = await AskedAsync();
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Og så send den. Stop.");
        Say(voice, "Send.");
        Say(voice, "Annullér.");
        Say(voice, "Stop");
        Say(voice, "Diktat slut.");
        await SaidStartingAsync(voice, "Sender 6 sætninger til issue 283");

        Assert.Equal((P283, $"{First} Og så send den. Stop. Send. Annullér. Stop"), Assert.Single(servers.Replies));
    }

    /// <summary>"Diktat slut" with an ellipsis, as the transcriber may end a trailing-off phrase (#507, #520), sends.</summary>
    [Fact]
    public async Task Diktat_slut_with_an_ellipsis_sends()
    {
        var (servers, voice) = await AskedAsync();
        await using var _ = voice;

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync(Started);
        Say(voice, First);
        Say(voice, "Diktat slut…");
        await SaidStartingAsync(voice, "Sender 1 sætning til issue 283");

        Assert.Equal((P283, First), Assert.Single(servers.Replies));
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
        Say(voice, "Diktat slut");
        await SaidStartingAsync(voice, "Sender 1 sætning til issue 283");
        Say(voice, "Hvad venter?");
        await Eventually.UntilAsync(() => model.Calls == 1, () => "the chat to hear what came after");

        Assert.Contains("Hvad venter?", Assert.Single(model.UserTexts));
        Assert.Single(servers.Replies);
    }

    /// <summary>The terminator as the last sentence of a final, after a sentence break, ends it, and the words before it are sent.</summary>
    [Fact]
    public async Task Diktat_slut_as_its_own_sentence_at_the_end_of_a_final_sends()
    {
        var (servers, voice) = await AskedAsync();
        await using var _ = voice;

        Say(voice, "Diktér til 283. Brug den eksisterende migration.");
        await voice.Events.SaidAsync(Started);
        Say(voice, "Det var det. Diktat slut.");
        await SaidStartingAsync(voice, "Sender 2 sætninger til issue 283");

        Assert.Equal((P283, "Brug den eksisterende migration. Det var det."), Assert.Single(servers.Replies));
    }

    /// <summary>
    /// #530: a project that is working takes a dictation as any reply, into the turn it is in; the read-back says it
    /// works, so the user knows it answers no question.
    /// </summary>
    [Fact]
    public async Task A_dictation_to_a_project_that_is_working_is_sent()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.AddProject(ServerA, "p/r/283", "283-voice", state: ProjectState.Running); return Task.CompletedTask; });
        await voice.Events.SaidAsync("Klar.");

        Say(voice, "Diktér til 283");
        await voice.Events.SaidAsync("Diktat til issue 283, voice. Sig diktat slut, eller annullér diktat.");
        Say(voice, First);
        Say(voice, "Diktat slut.");
        await voice.Events.SaidAsync("Sender 1 sætning til issue 283, der starter: Brug den eksisterende migration, ikke en ny. Den arbejder, og tager det med undervejs.");

        Assert.Equal((P283, First), Assert.Single(servers.Replies));
        // The model never heard a word of it
        Assert.Equal(0, model.Calls);
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

    /// <summary>
    /// The phrases, as the transcriber may render them: matched leniently on the phrase (case, punctuation, accents, a
    /// "c" for a "k", the two words run together or split by a full stop), strictly on it being a sentence of its own.
    /// </summary>
    [Theory]
    [InlineData("Diktat slut.", Dictation.Terminator.Send, "")]
    [InlineData("diktat slut", Dictation.Terminator.Send, "")]
    [InlineData("Diktat slut…", Dictation.Terminator.Send, "")]
    [InlineData("Diktat slut;", Dictation.Terminator.Send, "")]
    [InlineData("Diktat slut:", Dictation.Terminator.Send, "")]
    [InlineData("Diktat, slut!", Dictation.Terminator.Send, "")]
    [InlineData("Diktat-slut.", Dictation.Terminator.Send, "")]
    [InlineData("Diktatslut.", Dictation.Terminator.Send, "")]
    [InlineData("Dictat slut.", Dictation.Terminator.Send, "")]
    [InlineData("Diktat. Slut.", Dictation.Terminator.Send, "")]
    [InlineData("\"Diktat slut.\"", Dictation.Terminator.Send, "")]
    [InlineData("Send diktat.", Dictation.Terminator.Send, "")]
    [InlineData("End dictation.", Dictation.Terminator.Send, "")]
    [InlineData("Dictation end.", Dictation.Terminator.Send, "")]
    [InlineData("Send dictation.", Dictation.Terminator.Send, "")]
    [InlineData("Det var det. Diktat slut.", Dictation.Terminator.Send, "Det var det.")]
    [InlineData("Det var det. Diktat. Slut.", Dictation.Terminator.Send, "Det var det.")]
    [InlineData("Annullér diktat.", Dictation.Terminator.Cancel, "")]
    [InlineData("Annuller diktat", Dictation.Terminator.Cancel, "")]
    [InlineData("Slet diktat.", Dictation.Terminator.Cancel, "")]
    [InlineData("Cancel dictation.", Dictation.Terminator.Cancel, "")]
    [InlineData("Det var forkert. Annullér diktat.", Dictation.Terminator.Cancel, "Det var forkert.")]
    // #530: as the user was heard to say it, and with a filler in front of it
    [InlineData("Dictate end.", Dictation.Terminator.Send, "")]
    [InlineData("Stop dictation.", Dictation.Terminator.Send, "")]
    [InlineData("Diktér slut.", Dictation.Terminator.Send, "")]
    [InlineData("Dikter slut", Dictation.Terminator.Send, "")]
    [InlineData("Ja, øh, end dictation.", Dictation.Terminator.Send, "")]
    [InlineData("ja øh, diktat slut", Dictation.Terminator.Send, "")]
    [InlineData("Okay. Diktat slut.", Dictation.Terminator.Send, "Okay.")]
    [InlineData("Det var det. Øh. Diktat slut.", Dictation.Terminator.Send, "Det var det.")]
    [InlineData("Skal vi pushe? Ja. Diktat slut.", Dictation.Terminator.Send, "Skal vi pushe? Ja.")]
    [InlineData("Det var det. Uhm, cancel dictation.", Dictation.Terminator.Cancel, "Det var det.")]
    public void A_phrase_as_a_sentence_on_its_own_ends_it(string final, Dictation.Terminator terminator, string before) =>
        Assert.Equal((terminator, before), Dictation.Ends(final));

    /// <summary>A bare word, or a phrase inside a sentence, is the dictation's (#520).</summary>
    [Theory]
    [InlineData("Send.")]
    [InlineData("send den")]
    [InlineData("Send it.")]
    [InlineData("Stop")]
    [InlineData("Stop.")]
    [InlineData("Annullér.")]
    [InlineData("Cancel.")]
    [InlineData("Afbryd.")]
    [InlineData("Og så send den. Stop.")]
    [InlineData("og send den til Peter")]
    [InlineData("Send en besked til Peter.")]
    [InlineData("Stop ikke før testene er grønne.")]
    [InlineData("Skriv diktat slut i filen.")]
    [InlineData("Og så diktat slut.")]
    [InlineData("Diktat slut og send den.")]
    [InlineData("Det var det; diktat slut.")]
    [InlineData("Det var et diktat. Slut.")]
    [InlineData("Slut.")]
    [InlineData("Diktat.")]
    [InlineData("Ja, og skriv diktat slut i filen.")]
    [InlineData("Øh, men stop dictation først.")]
    public void Other_words_are_dictated(string final) => Assert.Equal(((Dictation.Terminator?)null, final), Dictation.Ends(final));

    /// <summary>The phrases are keyterms, so speech recognition hears them, and each fits in one.</summary>
    [Fact]
    public void The_phrases_are_keyterms() =>
        Assert.All(Dictation.Phrases, p =>
        {
            Assert.Contains(p.Phrase, GodModeGraph.CommandWords);
            Assert.InRange(p.Phrase.Length, 1, ElevenLabsLanguageOptions.MaxRealtimeKeytermLength);
        });

    /// <summary>What the bot tells the user to say ends a dictation as it says: in Danish and in English.</summary>
    [Theory]
    [InlineData("da-DK", "Sig diktat slut, eller annullér diktat.", "diktat slut", "annullér diktat")]
    [InlineData("en-US", "Say end dictation, or cancel dictation.", "end dictation", "cancel dictation")]
    public void The_phrases_the_bot_names_end_a_dictation(string language, string told, string send, string cancel)
    {
        Assert.EndsWith(told, new VoicePhrases(new SessionLanguages(language)).DictationStarted(new SpokenName("283")));
        Assert.Equal(Dictation.Terminator.Send, Dictation.Ends(send).Terminator);
        Assert.Equal(Dictation.Terminator.Cancel, Dictation.Ends(cancel).Terminator);
    }

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

    /// <summary>The mic closed, or voice stopped, with no terminator: dropped, and a later "diktat slut" sends nothing.</summary>
    [Fact]
    public async Task A_dictation_abandoned_is_dropped_and_never_sent()
    {
        var (servers, dictation, _) = await DictatingAsync();

        dictation.Abandon();

        Assert.False(dictation.Active);
        Assert.Null(await dictation.HearAsync("Diktat slut", CancellationToken.None));
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

        var said = await dictation.HearAsync("Diktat slut", CancellationToken.None);

        Assert.StartsWith("Intet sendt.", said);
        Assert.True(dictation.Active);
        Assert.Equal([First], dictation.Parts);
        Assert.Empty(servers.Replies);
    }

    /// <summary>#530: a project that started working while the user dictated is sent the dictation all the same.</summary>
    [Fact]
    public async Task A_send_to_a_project_that_started_working_is_sent()
    {
        var (servers, dictation, _) = await DictatingAsync();
        var waiting = await servers.GetStatusAsync(P283, CancellationToken.None);
        servers.SetStatus(ServerA, waiting with { State = ProjectState.Running });

        Assert.EndsWith("Den arbejder, og tager det med undervejs.", await dictation.HearAsync("Diktat slut", CancellationToken.None));
        Assert.False(dictation.Active);
        Assert.Equal((P283, First), Assert.Single(servers.Replies));
    }

    /// <summary>#530: "Diktér til" the same project, said while dictating, is no part: the dictation goes on, with what follows it.</summary>
    [Fact]
    public async Task Dictate_to_the_same_project_while_dictating_goes_on()
    {
        var (servers, dictation, _) = await DictatingAsync();

        Assert.Equal("Diktatet til issue 283 fortsætter.", await dictation.HearAsync("Diktér til 283.", CancellationToken.None));
        Assert.Equal([First], dictation.Parts);
        Assert.Equal("Diktatet til issue 283 fortsætter.", await dictation.HearAsync("Dictate to 283. Og kør testene.", CancellationToken.None));
        Assert.StartsWith("Sender 2 sætninger til issue 283", await dictation.HearAsync("Diktat slut.", CancellationToken.None));

        Assert.Equal($"{First} Og kør testene.", Assert.Single(servers.Replies).Text);
    }

    /// <summary>#530: "Diktér til" another project, or one no handle names, while dictating, is taken as nothing, and the dictation is kept.</summary>
    [Theory]
    [InlineData("Diktér til 101. Brug den nye.")]
    [InlineData("Dictate to GodMode Chat General.")]
    public async Task Dictate_to_another_project_while_dictating_adds_nothing(string final)
    {
        var (servers, dictation, _) = await DictatingAsync();
        servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?"), Question("p/r/101", "101-docs", "Hvilken?"));

        Assert.Equal("Du dikterer stadig til issue 283, intet tilføjet. Sig diktat slut eller annullér diktat først.",
            await dictation.HearAsync(final, CancellationToken.None));
        Assert.True(dictation.Active);
        Assert.Equal([First], dictation.Parts);
        Assert.Empty(servers.Replies);
    }

    /// <summary>#530: a dictated sentence that sounds like an end phrase is named in the read-back.</summary>
    [Fact]
    public async Task A_part_that_sounds_like_a_command_is_named_in_the_read_back()
    {
        var (servers, dictation, _) = await DictatingAsync();
        Assert.Null(await dictation.HearAsync("Dictation ended.", CancellationToken.None));

        Assert.EndsWith("En sætning lyder som en kommando: Dictation ended.", await dictation.HearAsync("Diktat slut", CancellationToken.None));
        Assert.Equal($"{First} Dictation ended.", Assert.Single(servers.Replies).Text);
    }

    [Theory]
    [InlineData("Dictate end.", true)]
    [InlineData("Stop dictating.", true)]
    [InlineData("Diktat færdig nu.", true)]
    [InlineData("Brug den eksisterende migration.", false)]
    [InlineData("Diktat-funktionen skal kunne afsluttes med et ja foran slutordene.", false)]
    public void Looks_like_a_command(string sentence, bool command) => Assert.Equal(command, Dictation.LooksLikeCommand(sentence));

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

        await dictation.HearAsync("Diktat slut", CancellationToken.None);

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
        Say(voice, "Diktat slut");
        await SaidStartingAsync(voice, "Sender 2 sætninger til issue 283");

        var dictated = $"{First} {Second}";
        await Eventually.UntilAsync(() => server.StdinOf(asking.Id).Count == 2, () => $"the dictation on stdin: {string.Join(" | ", server.StdinOf(asking.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(SpokenInput.Mark(dictated)), server.StdinOf(asking.Id)[1]);
        Assert.Equal(0, model.Calls);
    }
}
