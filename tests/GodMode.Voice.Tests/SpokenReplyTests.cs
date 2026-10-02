using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// A session's own spoken reply (issue #384), which it gave with <c>speak</c>, and the server carries on the attention
/// item (<see cref="AttentionItem.Spoken"/>): voice says it word for word, after a lead-in naming the project, in the
/// announcement, and through <c>project_status</c> and <c>what_needs_me</c> in place of the model's reply, which would
/// retell it. A turn with none is the model's to summarize, as before (#377).
/// </summary>
public sealed class SpokenReplyTests
{
    private const string ServerA = "server-a";
    private const string Id = "p/r/283";
    private const string Spoken = "Relayet virker nu, og testene er grønne. Skal jeg merge pull requesten?";
    private const string Written = "## Status\n\nRelayet forbinder igen efter rettelsen i SignalR.Proxy, og alle 126 tests er grønne. Skal jeg merge?";

    /// <summary>What the model says, retelling the reply: never said where the session gave its own.</summary>
    private const string Retold = "283 siger at relayet havde forbindelsesproblemer.";

    private static ProjectStatus Status(ProjectState state, string? question = null, string? result = null, string? spoken = null) =>
        new(Id, "283-voice", state, DateTime.UtcNow, DateTime.UtcNow, question, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0, RootName: "root", ProfileName: "Default", LastResult: result,
            LastResultAt: result is null ? null : DateTime.UtcNow, SpokenSummary: spoken);

    /// <summary>283 asks, with or without its own spoken reply, announced alone.</summary>
    private static async Task<(FakeServers Servers, OfflineVoice Voice)> AskedAsync(ScriptedChatClient model, string? spoken, string announced)
    {
        var servers = new FakeServers();
        var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Question(Id, "283-voice", Written) with { Spoken = spoken });
            servers.SetStatus(ServerA, Status(ProjectState.WaitingInput, question: Written, spoken: spoken));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync(announced);
        return (servers, voice);
    }

    /// <summary>The bot says <paramref name="text"/> once more after the announcement that said it first: the reply to the user.</summary>
    private static Task SaidAgainAsync(OfflineVoice voice, string text) =>
        Eventually.UntilAsync(() => voice.Events.Responses.Count(r => r == text) >= 2,
            () => $"the bot to say \"{text}\" in reply, after its announcement; it said: {string.Join(" | ", voice.Events.Responses)}");

    [Fact]
    public async Task A_spoken_reply_is_said_word_for_word_through_project_status_not_retold()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond(Retold);
        var (_, voice) = await AskedAsync(model, Spoken, $"283 spørger: {Spoken}");
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad spørger 283 om?");
        await SaidAgainAsync(voice, $"283 spørger: {Spoken}");

        Assert.DoesNotContain(Retold, voice.Events.Responses);
        Assert.Contains($"\"{Spoken}\", is said word for word by the system", Assert.Single(model.ToolResults));
    }

    [Fact]
    public async Task A_spoken_reply_is_said_word_for_word_through_what_needs_me()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe)
            .Respond($"1 venter: {Retold}");
        var (_, voice) = await AskedAsync(model, Spoken, $"283 spørger: {Spoken}");
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await SaidAgainAsync(voice, $"283 spørger: {Spoken}");

        Assert.DoesNotContain($"1 venter: {Retold}", voice.Events.Responses);
    }

    /// <summary>A turn without one falls back as today: the announcement is short, and the model says the status, from the full question.</summary>
    [Fact]
    public async Task A_turn_without_a_spoken_reply_falls_back_to_the_models_summary()
    {
        const string Summary = "283 spørger om den skal merge.";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond(Summary);
        var (_, voice) = await AskedAsync(model, spoken: null, "283 har et spørgsmål.");
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad spørger 283 om?");
        await voice.Events.SaidAsync(Summary);

        var result = Assert.Single(model.ToolResults);
        Assert.Contains(Written, result);
        Assert.DoesNotContain("spoken", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Of several, the list gives the spoken reply word for word for the model to say, and the model says the list.</summary>
    [Fact]
    public async Task Of_several_projects_the_list_gives_each_spoken_reply_as_it_is()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.Set(ServerA,
            Finished(Id, "283-voice", Written, minutesAgo: 6) with { Spoken = Spoken },
            Question("p/r/101", "101-docs", "Hvilken titel?"));

        var result = await tools.WhatNeedsMeAsync(CancellationToken.None);

        Assert.Contains($"- 283: finished: {Written} In its own spoken words: \"{Spoken}\"", result);
        Assert.Contains("- 101: question: Hvilken titel?", result);
        Assert.Empty(conversation.TakeSpoken());
    }

    [Fact]
    public void An_announcement_says_the_spoken_reply_after_the_projects_handle()
    {
        var phrases = new VoicePhrases(new VoiceBot.Core.Resources.SessionLanguages("da-DK"));

        Assert.Equal($"283 er færdig: {Spoken}", phrases.Announce("283", Finished(Id, "283-voice", Written) with { Spoken = Spoken }));
        Assert.Equal($"283 spørger: {Spoken}", phrases.Announce("283", Question(Id, "283-voice", Written) with { Spoken = Spoken }));
        Assert.Equal("283 er færdig", phrases.Announce("283", Finished(Id, "283-voice", Written)));
        // Its own '?' ends the sentence said
        Assert.Equal($"283 spørger: {Spoken}", GodModeAnnouncementFormatter.Sentence($"283 spørger: {Spoken}"));
    }

    /// <summary>
    /// A spoken reply that starts with "Sendt" is the session's, not the bot's claim of a send (#375): it is said after
    /// the handle, so it is never taken for one and replaced by "Intet sendt".
    /// </summary>
    [Fact]
    public async Task A_spoken_reply_that_starts_with_sendt_is_said_as_the_projects_not_taken_for_a_send()
    {
        const string SentForReview = "Sendt til review. Skal jeg merge, når den er godkendt?";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model, SentForReview, $"283 spørger: {SentForReview}");
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Status 283");
        await SaidAgainAsync(voice, $"283 spørger: {SentForReview}");

        Assert.Empty(servers.Replies);
        Assert.DoesNotContain("Intet sendt. Sig svaret igen.", voice.Events.Responses);
    }
}
