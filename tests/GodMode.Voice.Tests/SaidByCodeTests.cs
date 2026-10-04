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
        await voice.Events.SaidAsync($"{Danish.Several(2)} issue 101 har et spørgsmål. issue 283 er færdig.");

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await Eventually.UntilAsync(() => voice.Events.Responses.Count(r => r == "2 venter på dig: issue 101 har et spørgsmål. issue 283 er færdig.") == 2,
            () => $"the list said again, in the code's words; it said: {string.Join(" | ", voice.Events.Responses)}");

        Assert.Equal(1, model.Calls);
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
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-376-a", "376-x", root: "GodMode", kind: "issue", profile: "Mega");
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-382-b", "382-y", root: "GodMode", kind: "issue", profile: "Mega");
        servers.AddProject(ServerA, "Private/voicebot/260930-branch-master-c", "master", root: "voicebot", kind: "branch", profile: "Private");
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
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

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
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

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
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");
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
        servers.AddProject(ServerA, "Mega/GodMode/260930-issue-376-a", "376-x", root: "GodMode", kind: "issue", profile: "Mega");
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
        await voice.Events.SaidAsync("issue 283 fejlede.");

        voice.Transcriptions.SayAsRecognized("Status 283");
        await voice.Events.SaidAsync("issue 283 fejlede: build fejl.");

        Assert.Equal(2, model.Calls);
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

    [Fact]
    public void The_lists_are_worded_in_the_sessions_language()
    {
        var english = new VoicePhrases(new SessionLanguages("en-US"));
        var item = Question("p/r/283", "283-voice", "Push?");
        Assert.Equal("Nothing needs you.", english.Waiting([]));
        Assert.Equal("issue 283 has a question.", english.Waiting([(new SpokenName("issue 283"), item)]));
        Assert.Equal("No projects.", english.Projects([]));
        Assert.Equal("1 project. Profile Mega, root GodMode: issue 283.", english.Projects([("Mega", "GodMode", ["issue 283"])]));
        Assert.Equal("1 projekt. Profil Mega: issue 283.", Danish.Projects([("Mega", null, ["issue 283"])]));
    }
}
