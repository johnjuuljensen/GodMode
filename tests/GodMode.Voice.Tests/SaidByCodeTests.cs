using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #456: a tool result the code can say itself (what needs me, the projects, a short question or result) is said
/// in <see cref="VoicePhrases"/>' words, with one model call, the tool's: the respond round that would retell it is not
/// run (<see cref="CodeSaysInference"/>). An error, an unknown project, a root no project is in, or a text too long or
/// marked up to say as it is, goes back to the model, as before.
/// </summary>
public sealed class SaidByCodeTests
{
    private const string ServerA = "server-a";

    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));

    [Fact]
    public async Task What_needs_me_by_voice_is_one_model_call_said_in_the_codes_words()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 9),
                Finished("p/r/283", "283-voice", "Færdig med migrationen.", minutesAgo: 3));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync($"{Danish.Several(2)} issue 101, cleanup, har et spørgsmål. issue 283, voice, er færdig.");

        // Announced most urgent first, the question before the result (#462); listed the most recent first (#468)
        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("2 venter på dig: issue 283 er færdig. issue 101, cleanup, har et spørgsmål.");

        Assert.Equal(1, model.Calls);
    }

    /// <summary>
    /// #507: two asks in one breath. The first call says what more the user asked for (<see cref="VoiceTools.ThenParameter"/>),
    /// so the code's words for it wait and the model makes the second call; the code says both, in the order asked.
    /// </summary>
    [Fact]
    public async Task What_needs_me_and_then_a_status_in_one_breath_are_both_said()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe, new() { [VoiceTools.ThenParameter] = "læs 283" })
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" });
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe til master?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Hvad venter, og læs 283");
        await voice.Events.SaidAsync("issue 283 har et spørgsmål. issue 283 spørger: Skal jeg pushe til master?");

        Assert.Equal(2, model.Calls);
        // The model's second round knew the first was said, and what was left to do
        var told = Assert.Single(model.ToolResults);
        Assert.Contains("The system has said this to the user itself already", told);
        Assert.Contains("\"læs 283\"", told);
    }

    /// <summary>The model's own reply after a held part (an unknown project it explains) is said after the code's words.</summary>
    [Fact]
    public async Task A_second_ask_the_model_answers_itself_is_said_after_the_codes_words()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe, new() { [VoiceTools.ThenParameter] = "status 999" })
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "999" })
            .Respond("Ukendt: 999.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter, og status 999");
        await voice.Events.SaidAsync("Intet venter. Ukendt: 999.");

        Assert.Equal(3, model.Calls);
    }

    /// <summary>
    /// #523: the code's words for a first part are said even when the model never ends the turn: it calls tool after tool
    /// until VoiceBot's round limit, and gives no reply. What was held is not lost with it.
    /// </summary>
    [Fact]
    public async Task Held_words_are_said_when_the_model_hits_the_round_limit()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe, new() { [VoiceTools.ThenParameter] = "status 999" });
        for (var i = 0; i < 9; i++)
            model.CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "999" });
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter, og status 999");
        await voice.Events.SaidAsync("Intet venter.");

        Assert.Equal(10, model.Calls);
    }

    /// <summary>
    /// #375 behind held words (#507): the model claims a send after a held part, and no answer went out. Its "Sendt"
    /// no longer starts the reply, but it is still not said: the user hears that nothing was sent.
    /// </summary>
    [Fact]
    public async Task A_send_claimed_after_a_held_part_with_none_sent_is_not_said()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe, new() { [VoiceTools.ThenParameter] = "svar 283 ja" })
            .Respond("Sendt til 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter, og svar 283 ja");
        await voice.Events.SaidAsync("Intet venter. Intet sendt. Sig svaret igen.");

        Assert.Empty(servers.Replies);
        Assert.DoesNotContain(voice.Events.Responses, r => r.Contains("Sendt", StringComparison.Ordinal));
    }

    /// <summary>The model ends the turn on a protocol word alone after a held part: the code's words are said, not "Klar".</summary>
    [Fact]
    public async Task A_one_word_reply_after_a_held_part_adds_nothing()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe, new() { [VoiceTools.ThenParameter] = "og så?" })
            .Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter, og så?");
        await voice.Events.SaidAsync("Intet venter.");

        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task Nothing_waiting_is_said_by_the_code()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");

        Assert.Equal(1, model.Calls);
    }

    /// <summary>A filter the user asked for ("i Mega") is the tool's argument, so what the code says is what was asked about.</summary>
    [Fact]
    public async Task The_projects_of_one_profile_are_said_by_the_code_and_only_those()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-376-a", "376-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 2);
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-382-b", "382-y", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 1);
        servers.AddProject(ServerA, "Private/voicebot/260930-branch-master-c", "master", root: "voicebot", kind: "branch", profile: "Private", minutesAgo: 3);
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ListProjects, new() { [VoiceTools.RootParameter] = "mega" })
            .CallTool(VoiceTools.ListProjects);
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvilke projekter er der i Mega?");
        await voice.Events.SaidAsync("2 projekter. Profil Mega, root GodMode: issue 382, issue 376.");
        voice.Transcriptions.SayAsRecognized("Hvilke projekter er der?");
        await voice.Events.SaidAsync("3 projekter. Profil Mega, root GodMode: issue 382, issue 376. Profil Private, root voicebot: branch master.");

        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task A_short_question_is_read_by_the_code_through_project_status()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" });
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe til master?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Hvad spørger 283 om?");
        await voice.Events.SaidAsync("issue 283 spørger: Skal jeg pushe til master?");

        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task An_unknown_project_goes_back_to_the_model()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "999" })
            .Respond("Ukendt. Mente du issue 283?");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Status 999");
        await voice.Events.SaidAsync("Ukendt. Mente du issue 283?");

        Assert.Equal(2, model.Calls);
        Assert.StartsWith("Unknown project '999'.", Assert.Single(model.ToolResults));
    }

    /// <summary>A tool that throws (the server fails to read the project's status) gives the model its error, which it says.</summary>
    [Fact]
    public async Task A_tool_that_fails_goes_back_to_the_model()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Status for issue 283 kunne ikke hentes.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");
        servers.StatusError = new InvalidOperationException("The server is gone.");

        voice.Transcriptions.SayAsRecognized("Status 283");
        await voice.Events.SaidAsync("Status for issue 283 kunne ikke hentes.");

        Assert.Equal(2, model.Calls);
        Assert.Equal("Error: The server is gone.", Assert.Single(model.ToolResults));
    }

    [Fact]
    public async Task A_root_no_project_is_in_goes_back_to_the_model()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-376-a", "376-x", root: "GodMode", kind: "issue", profile: "Mega", minutesAgo: 2);
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ListProjects, new() { [VoiceTools.RootParameter] = "kappe" })
            .Respond("Ingen projekter i kappe.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvilke projekter er der i kappe?");
        await voice.Events.SaidAsync("Ingen projekter i kappe.");

        Assert.Equal(2, model.Calls);
        Assert.Equal("No root or profile 'kappe' has any project. Nothing was listed. There are: Profile Mega, root GodMode.", Assert.Single(model.ToolResults));
    }

    [Fact]
    public async Task A_failed_project_status_goes_back_to_the_model()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("issue 283 fejlede: build fejl.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, new AttentionItem("p/r/283", "283-voice", "Default", "root", AttentionKind.Error, DateTime.UtcNow, "Build failed"));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("issue 283, voice, fejlede.");

        voice.Transcriptions.SayAsRecognized("Status 283");
        await voice.Events.SaidAsync("issue 283 fejlede: build fejl.");

        Assert.Equal(2, model.Calls);
    }

    private const string Recap = "Pull request 456 er åben, testene er grønne, den venter på review.";

    private static ProjectStatus Status(ProjectState state, string? result = null, string? spoken = null, string? recap = null) =>
        new("p/r/283", "283-voice", state, DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0, RootName: "root", ProfileName: "Default", LastResult: result,
            LastResultAt: result is null ? null : DateTime.UtcNow, SpokenSummary: spoken, Recap: recap, RecapAt: recap is null ? null : DateTime.UtcNow);

    /// <summary>
    /// What the code says of <paramref name="status"/> through project_status, beside <paramref name="item"/> if given;
    /// null when the model says it.
    /// </summary>
    private static async Task<string?> StatusSaidAsync(ProjectStatus status, AttentionItem? item = null)
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        if (item is not null) servers.Set(ServerA, item);
        servers.SetStatus(ServerA, status);
        return conversation.TakeSaid(await tools.ProjectStatusAsync("283", CancellationToken.None));
    }

    /// <summary>#466's order, in the code's words: the recap first, over the spoken reply and the result.</summary>
    [Theory]
    [InlineData(ProjectState.Running)]
    [InlineData(ProjectState.Idle)]
    public async Task The_recap_is_said_by_the_code_over_the_spoken_reply_and_the_result(ProjectState state) =>
        Assert.Equal($"issue 283, voice: {Recap}", await StatusSaidAsync(Status(state, result: "Alt er grønt.", spoken: "Rettelsen er pushet.", recap: Recap)));

    [Fact]
    public async Task Beside_an_item_the_recap_is_said_before_what_needs_the_user() =>
        Assert.Equal($"issue 283, voice: {Recap} issue 283 er færdig: Færdig med migrationen.",
            await StatusSaidAsync(Status(ProjectState.Idle, result: "Færdig med migrationen.", recap: Recap),
                Finished("p/r/283", "283-voice", "Færdig med migrationen.")));

    /// <summary>A finished turn that said no outcome is idle in the code's words too, never done (#467).</summary>
    [Fact]
    public async Task A_finished_turn_with_no_outcome_is_said_as_idle() =>
        Assert.Equal($"issue 283, voice: {Recap} issue 283 er idle: Venter på CI.",
            await StatusSaidAsync(Status(ProjectState.Idle, result: "Venter på CI.", recap: Recap),
                Finished("p/r/283", "283-voice", "Venter på CI.", outcome: null)));

    /// <summary>A recap beside a question too long to say as it is: the model says both, so neither is lost.</summary>
    [Fact]
    public async Task A_recap_beside_a_question_the_code_cannot_say_goes_to_the_model() =>
        Assert.Null(await StatusSaidAsync(Status(ProjectState.WaitingInput, recap: Recap) with { CurrentQuestion = "## Valg\n\nSkal jeg `merge`?" },
            Question("p/r/283", "283-voice", "## Valg\n\nSkal jeg `merge`?")));

    [Fact]
    public async Task Without_a_recap_or_an_item_the_spoken_reply_then_the_result_is_said_by_the_code()
    {
        Assert.Equal("issue 283, voice, sagde sidst: Rettelsen er pushet.",
            await StatusSaidAsync(Status(ProjectState.Idle, result: "Alt er grønt.", spoken: "Rettelsen er pushet.")));
        Assert.Equal("Sidste resultat fra issue 283, voice: Alt er grønt.", await StatusSaidAsync(Status(ProjectState.Idle, result: "Alt er grønt.")));

        // A long plain result is said shortened, as the tool's text has it
        var shortened = await StatusSaidAsync(Status(ProjectState.Idle, result: string.Join(" ", Enumerable.Repeat("Testene er grønne", 40))));
        Assert.StartsWith("Sidste resultat fra issue 283, voice: Testene er grønne", shortened);
        Assert.EndsWith("…", shortened);
    }

    [Fact]
    public async Task A_marked_up_result_or_nothing_to_read_goes_to_the_model()
    {
        Assert.Null(await StatusSaidAsync(Status(ProjectState.Idle, result: "## Done\n\n- `ProjectHub.cs` fixed")));
        Assert.Null(await StatusSaidAsync(Status(ProjectState.Idle)));
    }

    [Theory]
    [InlineData("Skal jeg pushe til master?", true)]
    [InlineData("Færdig: alle 126 tests er grønne, og PR'en er klar.", true)]
    [InlineData("## Status\n\nAlt er grønt.", false)]
    [InlineData("Ret `ProjectHub.cs` først?", false)]
    [InlineData("Skal jeg slette src/GodMode.Voice?", false)]
    [InlineData("Brug **den** gamle?", false)]
    [InlineData("Første afsnit.\n\nAndet afsnit.", false)]
    [InlineData("   ", false)]
    public void A_text_is_said_as_it_is_only_when_short_and_plain(string text, bool said) =>
        Assert.Equal(said, VoiceTools.SaidAsIs(text));

    [Fact]
    public void A_text_over_the_limit_is_the_models_to_shorten()
    {
        Assert.True(VoiceTools.SaidAsIs(new string('a', VoiceTools.SaidAsIsLength)));
        Assert.False(VoiceTools.SaidAsIs(new string('a', VoiceTools.SaidAsIsLength + 1)));
    }

    /// <summary>#507: the issue's test. A Danish session where the user switches to English gets the code's words in English, and back.</summary>
    [Fact]
    public async Task The_codes_words_follow_the_user_switching_to_English_and_back()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe).CallTool(VoiceTools.WhatNeedsMe).CallTool(VoiceTools.WhatNeedsMe);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("What needs me?");
        await voice.Events.SaidAsync("Nothing needs you.");
        // A final that tells no language keeps the one spoken last
        voice.Transcriptions.SayAsRecognized("Status?");
        await Eventually.UntilAsync(() => voice.Events.Responses.Count(r => r == "Nothing needs you.") == 2,
            () => $"English again; it said: {string.Join(" | ", voice.Events.Responses)}");
        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");
    }

    [Theory]
    [InlineData("Hvad venter?", 2)]
    [InlineData("Læs 283", 1)]
    [InlineData("Svar at den skal merge the branch", 2)]
    [InlineData("What needs me?", -3)]
    [InlineData("Read the reply of issue 283", -4)]
    [InlineData("Status 283", 0)]
    [InlineData("pull request", 0)]
    public void The_language_of_a_final_is_told_by_its_small_words(string final, int lean) =>
        Assert.Equal(lean, SpokenLanguage.Lean(final));

    /// <summary>
    /// A few English words in a Danish session may be Danish misheard ("Nej, som overseer" as "Now as overseer", #449):
    /// only a sentence of English switches, and one Danish word switches back.
    /// </summary>
    [Fact]
    public void A_few_english_words_switch_nothing_and_a_danish_word_switches_back()
    {
        var phrases = new VoicePhrases(new SessionLanguages("da-DK"));
        phrases.Heard("Now as overseer.");
        phrases.Heard("No");
        Assert.True(phrases.Danish);
        phrases.Heard("What needs me?");
        Assert.False(phrases.Danish);
        phrases.Heard("Status 283");
        Assert.False(phrases.Danish);
        phrases.Heard("Mere");
        Assert.True(phrases.Danish);

        var english = new VoicePhrases(new SessionLanguages("en-US"));
        english.Heard("Hvad venter?");
        Assert.True(english.Danish == false);
        english.Heard("Hvad venter der nu?");
        Assert.True(english.Danish);
        english.Heard("More");
        Assert.False(english.Danish);
    }

    /// <summary>
    /// #523: an English filler "er", or "min" for minutes, is no Danish word: after a switch to English, a final with one
    /// of them keeps the code's words in English. A Danish sentence is still told by its other words.
    /// </summary>
    [Theory]
    [InlineData("Er, status 283")]
    [InlineData("Er")]
    [InlineData("283, 30 min")]
    [InlineData("Er… 283, min")]
    public void A_filler_er_or_min_does_not_switch_back_to_danish(string final)
    {
        var phrases = new VoicePhrases(new SessionLanguages("da-DK"));
        phrases.Heard("What needs me?");
        Assert.False(phrases.Danish);

        phrases.Heard(final);

        Assert.False(phrases.Danish);
        phrases.Heard("Hvad er status?");
        Assert.True(phrases.Danish);
    }

    [Fact]
    public void The_lists_are_worded_in_the_sessions_language()
    {
        var english = new VoicePhrases(new SessionLanguages("en-US"));
        var item = Question("p/r/283", "283-voice", "Push?");
        Assert.Equal("Nothing needs you.", english.Waiting([]));
        Assert.Equal("issue 283 has a question.", english.Waiting([(new SpokenName("issue 283"), item)]));
        Assert.Equal("No projects.", english.Projects([]));
        Assert.Equal("1 project. Profile Mega, root GodMode: issue 283.", english.Projects([new("Mega", "GodMode", ["issue 283"])]));
        Assert.Equal("1 projekt. Profil Mega: issue 283.", Danish.Projects([new("Mega", null, ["issue 283"])]));
    }
}
