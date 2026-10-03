using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>A whole voice session over servers in memory: what it announces, and where an answer goes.</summary>
public sealed class VoiceSessionTests
{
    private const string ServerA = "server-a";
    private const string ServerB = "server-b";

    [Fact]
    public async Task What_waits_at_the_start_is_announced_after_the_greeting_and_a_new_item_when_it_comes()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient(),
            connect: _ => { servers.Set(ServerA, Question("p/r/101-cleanup", "101-cleanup", "Skal jeg slette de gamle kolonner?")); return Task.CompletedTask; });

        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");
        servers.Set(ServerB, Permission("p/r/283-voice", "283-voice", "Bash: git push origin feature/283"));
        await voice.Events.SaidAsync("issue 283 skal have tilladelse: Bash: git push origin feature/283. Svar på skærmen.");

        Assert.Equal(["Klar.", "issue 101 har et spørgsmål.", "issue 283 skal have tilladelse: Bash: git push origin feature/283. Svar på skærmen."], voice.Events.Responses);
    }

    [Fact]
    public async Task An_item_is_announced_once_however_often_its_list_is_pushed_again()
    {
        var servers = new FakeServers();
        var item = Question("p/r/101", "101-cleanup", "Hvilken branch?");
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());

        servers.Set(ServerA, item);
        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");
        servers.Set(ServerA, item);   // a reconnect takes the whole list again
        servers.Set(ServerA, item, Question("p/r/102", "102-docs", "Dansk eller engelsk?", minutesAgo: 1));
        await voice.Events.SaidAsync("issue 102 har et spørgsmål.");

        Assert.Single(voice.Events.Responses, r => r == "issue 101 har et spørgsmål.");
    }

    [Fact]
    public async Task Several_at_once_are_said_after_their_count()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient(), connect: _ =>
        {
            servers.Set(ServerA, Question("p/r/101", "101-a", "?"), Question("p/r/102", "102-b", "?"));
            servers.Set(ServerB, Question("p/r/103", "103-c", "?"));
            return Task.CompletedTask;
        });

        await voice.Events.SaidAsync("3 venter på dig: issue 101 har et spørgsmål. issue 102 har et spørgsmål. issue 103 har et spørgsmål.");
    }

    /// <summary>
    /// Answer routing: an answer that names no project goes to the one announced last, not to another that also
    /// waits. The model is scripted to leave the project out, as the prompt tells it to for "Svar at …".
    /// </summary>
    [Fact]
    public async Task An_answer_that_names_no_project_goes_to_the_one_announced()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Brug den eksisterende migration." })
            .Respond("Sendt til 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 30)); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");
        servers.Set(ServerB, Question("p/r/283", "283-voice", "Ny migration eller den eksisterende?"));
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

        voice.Transcriptions.AddFinal("Svar at den skal bruge den eksisterende migration");
        await voice.Events.SaidAsync("Sendt til issue 283.");

        var (project, text) = Assert.Single(servers.Replies);
        Assert.Equal(new ProjectRef(ServerB, "p/r/283"), project);
        Assert.Equal("Brug den eksisterende migration.", text);
    }

    [Fact]
    public async Task An_answer_names_a_project_by_its_number_said_in_Danish()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "hundrede og et", [VoiceTools.TextParameter] = "Ja, slet dem." })
            .Respond("Sendt til 101.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 30), Question("p/r/283", "283-voice", "Migration?"));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("2 venter på dig: issue 101 har et spørgsmål. issue 283 har et spørgsmål.");

        voice.Transcriptions.AddFinal("Svar hundrede og et at den skal slette dem");
        await voice.Events.SaidAsync("Sendt til issue 101.");

        Assert.Equal(new ProjectRef(ServerA, "p/r/101"), Assert.Single(servers.Replies).Project);
    }

    /// <summary>
    /// A number is its project, not the handle closest to it: "28" is a project that has not needed the user yet,
    /// not 283, which has (their Jaro-Winkler score is about 0.91).
    /// </summary>
    [Fact]
    public async Task An_answer_to_a_number_goes_to_that_project_not_to_a_handle_like_it()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "p/r/28-x", "28-x");
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "28", [VoiceTools.TextParameter] = "Kør testene." })
            .Respond("Sendt til 28.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerB, Question("p/r/283", "283-voice", "Migration?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

        voice.Transcriptions.AddFinal("Svar 28 at den skal køre testene");
        await voice.Events.SaidAsync("Sendt til issue 28.");

        Assert.Equal(new ProjectRef(ServerA, "p/r/28-x"), Assert.Single(servers.Replies).Project);
    }

    /// <summary>
    /// What "what needs me" read out is what the conversation is about: its one project, or none when it read out
    /// several, so an unnamed answer never goes to a project talked about before.
    /// </summary>
    [Fact]
    public async Task What_needs_me_makes_its_one_project_the_one_answered_and_several_none()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var conversation = new VoiceConversation { Current = new ProjectRef(ServerA, "p/r/101") };
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.Set(ServerB, Question("p/r/283", "283-voice", "Migration?"));

        await tools.WhatNeedsMeAsync(CancellationToken.None);
        Assert.Equal(new ProjectRef(ServerB, "p/r/283"), conversation.Current);

        servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?"));
        await tools.WhatNeedsMeAsync(CancellationToken.None);
        Assert.Null(conversation.Current);
        Assert.StartsWith("No project is being talked about", await tools.AnswerAsync(null, "Brug den eksisterende.", CancellationToken.None));
        Assert.Empty(servers.Replies);
    }

    /// <summary>No permission is granted or denied by voice: ReplyAndResume would deny one with the text.</summary>
    [Fact]
    public async Task A_permission_request_is_not_answered_by_voice()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja, gør det." })
            .Respond("283 skal have tilladelse. Svar på skærmen.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Permission("p/r/283", "283-voice", "Bash: rm -rf build"));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("issue 283 skal have tilladelse: Bash: rm -rf build. Svar på skærmen.");

        voice.Transcriptions.AddFinal("ja gør det");
        await voice.Events.SaidAsync("283 skal have tilladelse. Svar på skærmen.");

        Assert.Empty(servers.Replies);
        Assert.Contains(model.ToolResults, r => r.Contains("answered on screen") && r.Contains("Nothing was sent"));
    }

    /// <summary>
    /// The same answer twice in a row, each to its own question, is answered twice: neither the session nor the
    /// chat node takes the second "ja" for a repeat of the first (VoiceBot#39). Said as ElevenLabs sends it.
    /// </summary>
    [Fact]
    public async Task The_same_answer_twice_to_two_questions_is_answered_both_times()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja." }).Respond("Sendt til 101.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja." }).Respond("Sendt til 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Skal jeg slette kolonnerne?", minutesAgo: 30)); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("ja");
        await voice.Events.SaidAsync("Sendt til issue 101.");
        servers.Set(ServerB, Question("p/r/283", "283-voice", "Skal jeg bruge den eksisterende migration?"));
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");
        voice.Transcriptions.SayAsRecognized("ja");
        await voice.Events.SaidAsync("Sendt til issue 283.");

        Assert.Equal([new ProjectRef(ServerA, "p/r/101"), new ProjectRef(ServerB, "p/r/283")], servers.Replies.Select(r => r.Project));
        Assert.Equal(4, model.Calls);
    }

    /// <summary>"ja" and "nej" are answers: no noise filter may drop them before they reach the model.</summary>
    [Theory]
    [InlineData("ja")]
    [InlineData("nej")]
    [InlineData("tak")]
    public async Task A_spoken_yes_or_no_reaches_the_model(string answer)
    {
        var model = new ScriptedChatClient().Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddFinal(answer);

        await Eventually.UntilAsync(() => model.UserTexts.Any(t => t.EndsWith($"Text: {answer}")),
            () => $"the model to get \"{answer}\"; it got: {string.Join(" | ", model.UserTexts)}");
    }

    /// <summary>The button shows what the session reports it is doing (johnjuuljensen/VoiceBot#35), and off once it stopped.</summary>
    [Fact]
    public async Task The_state_is_what_the_session_is_doing()
    {
        var model = new ScriptedChatClient().Respond("Intet venter.");
        var voice = await OfflineVoice.StartAsync(new FakeServers(), model, speech: TimeSpan.FromMilliseconds(500));
        await using (voice)
        {
            await voice.Events.SaidAsync("Klar.");
            await ListeningAsync(voice);
            var before = voice.Events.States.Count;

            voice.Transcriptions.AddFinal("Hvad venter på mig?");
            await voice.Events.SaidAsync("Intet venter.");
            await ListeningAsync(voice);

            Assert.Equal([VoiceState.Thinking, VoiceState.Speaking, VoiceState.Listening], voice.Events.States.Skip(before));
        }
        Assert.Equal(VoiceState.Off, voice.Events.States.Last());

        static Task ListeningAsync(OfflineVoice voice) => Eventually.UntilAsync(() => voice.Session.State == VoiceState.Listening,
            () => $"the session to listen; it is {voice.Session.State}, after {string.Join(", ", voice.Events.States)}");
    }

    /// <summary>
    /// A microphone that failed mid-session faults the transcription source's teardown (VoiceBot's
    /// SpeechEngineTranscriptionSource rethrows it): the session is stopped all the same, and shows off.
    /// </summary>
    [Fact]
    public async Task A_session_whose_microphone_failed_stops()
    {
        var microphone = new ScriptedAudioSource();
        var voice = await OfflineVoice.StartAsync(new FakeServers(), new ScriptedChatClient(), microphone: microphone);
        await voice.Events.SaidAsync("Klar.");

        microphone.Fail(new InvalidOperationException("The microphone stopped delivering audio"));
        await voice.DisposeAsync();

        Assert.Equal(VoiceState.Off, voice.Events.States.Last());
    }

    /// <summary>
    /// VoiceBot#64 reports a microphone that fails while the session runs (its AudioInput): the app hears it as the voice
    /// session's failure, which it names, and voice shows the error until it is stopped, since nothing brings it back.
    /// </summary>
    [Fact]
    public async Task A_microphone_that_fails_is_reported_and_held_as_an_error()
    {
        var microphone = new ScriptedAudioSource();
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), new ScriptedChatClient(), microphone: microphone);
        await voice.Events.SaidAsync("Klar.");

        microphone.Fail(new InvalidOperationException("The microphone stopped delivering audio"));

        await Eventually.UntilAsync(() => !voice.Events.Errors.IsEmpty, () => "the microphone's failure to be reported");
        Assert.Equal((SessionService.Session, SessionErrorKind.ServiceError, "The microphone stopped: The microphone stopped delivering audio"),
            Assert.Single(voice.Events.Errors));
        await Eventually.UntilAsync(() => voice.Events.States.LastOrDefault() == VoiceState.Error,
            () => $"voice to show the error; its states: {string.Join(", ", voice.Events.States)}");
    }

    /// <summary>A held error goes only with its own service's recovery: an STT reconnect is no working microphone.</summary>
    [Fact]
    public void A_held_error_is_released_by_the_failed_services_recovery_only()
    {
        var state = new VoiceStateTracker();
        state.Release();
        state.Hold(VoiceState.Error, SessionService.AudioInput);

        state.Release(SessionService.SpeechRecognition);
        Assert.Equal(VoiceState.Error, state.Current);

        state.Release(SessionService.AudioInput);
        Assert.Equal(VoiceState.Listening, state.Current);
    }

    [Theory]
    [InlineData(VoiceSettings.DefaultLanguage)]
    [InlineData("da-DK")]
    [InlineData("en")]
    public void No_noise_word_is_an_answer(string language) =>
        Assert.DoesNotContain(VoiceSession.NoiseWords(VoiceSettings.ParseLanguages(language)), VoiceSession.AnswerWords.Contains);

    /// <summary>
    /// VoiceBot's lists (ah, hej, hey, hmm, oh, øh for da-DK+en, since "tak" left its Danish one,
    /// johnjuuljensen/VoiceBot#47), without the greetings: "Hej" is said on purpose.
    /// </summary>
    [Fact]
    public void A_Danish_session_with_English_drops_the_ghost_words_but_not_the_greetings() =>
        Assert.Equal(["ah", "hmm", "oh", "øh"],
            VoiceSession.NoiseWords(VoiceSettings.Default.Languages).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(VoiceSettings.DefaultLanguage)]
    [InlineData("da-DK")]
    [InlineData("en")]
    public void No_noise_word_is_a_greeting(string language) =>
        Assert.DoesNotContain(VoiceSession.NoiseWords(VoiceSettings.ParseLanguages(language)), VoiceSession.Greetings.Contains);

    [Fact]
    public async Task Hej_is_heard_not_filtered_as_noise()
    {
        var model = new ScriptedChatClient().Respond("Hej. Hvad kan jeg gøre?");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddFinal("Hej.");

        await voice.Events.SaidAsync("Hej. Hvad kan jeg gøre?");
    }

    /// <summary>
    /// Speech recognition is biased towards the names the servers have now: a project created after the start brings
    /// its root, profile and handle, and one deleted takes its handle along (VoiceBot#51 renews a running session's terms).
    /// </summary>
    [Fact]
    public async Task The_keyterms_follow_the_roots_profiles_and_sessions_as_they_come_and_go()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "Work/server-configs/260930-feat-vonage-a1", "feature/vonage-trunk", root: "server-configs", kind: "feat", profile: "Work");
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());

        Assert.Equal(voice.Session.SttKeyterms, voice.Providers.Language!.SttKeyterms);
        Assert.Contains("server-configs", voice.Session.SttKeyterms);
        Assert.Contains("Work", voice.Session.SttKeyterms);
        Assert.Contains("vonage", voice.Session.SttKeyterms);
        Assert.DoesNotContain("Assistant", voice.Session.SttKeyterms);

        servers.AddProject(ServerB, "Outbound/Assistant/260930-chat-kappe-b2", "Kappe", root: "Assistant", kind: "chat", profile: "Outbound");
        Assert.Equal(
            [.. GodModeGraph.CommandWords, "Assistant", "server-configs", "Outbound", "Work", "kappe", "vonage"],
            voice.Session.SttKeyterms);

        servers.DeleteProject(ServerA, "Work/server-configs/260930-feat-vonage-a1");
        Assert.Equal([.. GodModeGraph.CommandWords, "Assistant", "Outbound", "kappe"], voice.Session.SttKeyterms);
    }

    /// <summary>
    /// Each new set of terms is a new ElevenLabs connection: a status update reorders the projects, not their names, so
    /// two sessions updating in turn renew nothing, and only a session added or deleted does, once.
    /// </summary>
    [Fact]
    public async Task Status_updates_that_only_reorder_the_keyterms_do_not_renew_them()
    {
        const string vonage = "Work/server-configs/260930-feat-vonage-a1", kappe = "Outbound/Assistant/260930-chat-kappe-b2";
        var servers = new FakeServers();
        void Update(string server, string id) => servers.AddProject(server, id, id == vonage ? "feature/vonage-trunk" : "Kappe",
            root: id == vonage ? "server-configs" : "Assistant", kind: id == vonage ? "feat" : "chat", profile: id == vonage ? "Work" : "Outbound");
        Update(ServerA, vonage);
        Update(ServerB, kappe);
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());
        var sent = voice.Session.SttKeyterms;

        for (var i = 0; i < 4; i++)
        {
            Update(ServerA, vonage);
            Assert.NotEqual(sent, VoiceSession.Keyterms(voice.Session.Projects.Projects, voice.Session.Handles));   // reordered
            Update(ServerB, kappe);
        }
        Assert.Same(sent, voice.Session.SttKeyterms);

        servers.AddProject(ServerB, "Outbound/Assistant/260930-chat-budget-c3", "Budget", root: "Assistant", kind: "chat", profile: "Outbound");
        var added = voice.Session.SttKeyterms;
        Assert.NotSame(sent, added);
        Assert.Contains("budget", added);
        Update(ServerA, vonage);
        Assert.Same(added, voice.Session.SttKeyterms);

        servers.DeleteProject(ServerB, "Outbound/Assistant/260930-chat-budget-c3");
        Assert.NotSame(added, voice.Session.SttKeyterms);
        Assert.DoesNotContain("budget", voice.Session.SttKeyterms);
    }

    /// <summary>VoiceBot stops announcing for good when its formatter throws (johnjuuljensen/VoiceBot#27).</summary>
    [Fact]
    public void A_formatter_that_throws_once_does_not_end_the_announcements()
    {
        var formatter = new NeverThrowingFormatter(new ThrowsOnce(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal("issue 101 har et spørgsmål.", formatter.Format([new Announcement("issue 101 har et spørgsmål")], new SessionLanguages("da-DK")));
        Assert.Equal("fine: 102", formatter.Format([new Announcement("102")], new SessionLanguages("da-DK")));
    }

    [Fact]
    public async Task Announcements_go_on_for_the_whole_session()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());

        for (var n = 101; n <= 104; n++)
        {
            servers.Set(ServerA, Question($"p/r/{n}", $"{n}-x", "?"));
            await voice.Events.SaidAsync($"issue {n} har et spørgsmål.");
        }
    }

    private sealed class ThrowsOnce : IAnnouncementFormatter
    {
        private int _calls;

        public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("formatter bug")
                : $"fine: {announcements[0].Text}";
    }
}
