using System.Text.Json;
using GodMode.ClientBase.Hub;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;

namespace GodMode.Voice.Tests;

/// <summary>
/// A voice session against the real server, whose projects run FakeClaude: text in place of speech, a silent
/// synthesizer and sink, a scripted model. No audio, speech service or key.
/// </summary>
public sealed class EndToEndTests
{
    private const string OlderQuestion = "Skal jeg slette de gamle kolonner?";
    private const string Question = "Skal jeg bruge den eksisterende migration, eller lave en ny?";
    private const string Answer = "Den skal bruge den eksisterende migration.";

    /// <summary>A turn that ends on a question in plain text, then waits for the answer and takes another turn.</summary>
    private static FakeScript Asking(string question) =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant(question).Sleep(50).EmitResult(question)
            .AwaitStdin().EmitAssistant("Okay.").Sleep(50).EmitResult("Okay.");

    [Fact]
    public async Task A_question_is_announced_by_its_handle_and_the_spoken_answer_reaches_that_session()
    {
        await using var server = await TestServer.StartAsync(Asking(OlderQuestion));
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();

        // One project already waits when voice starts; it is not the one answered
        var older = await CreateAsync(hub, "101-drop-columns");
        await WaitForAttentionAsync(hub, older.Id);

        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.WhatNeedsMe).Respond("2 venter: 101 og 283 har spørgsmål.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "283", [VoiceTools.TextParameter] = Answer }).Respond("Sendt til 283.");
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");

        // A project asks while voice is on: the bot names it
        server.UseScript(Asking(Question));
        var asking = await CreateAsync(hub, "283-add-migration");
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

        // As ElevenLabs sends it: a partial, then a final with the same text. The final reaches the model once
        voice.Transcriptions.SayAsRecognized("Hvad venter på mig?");
        await voice.Events.SaidAsync("2 venter: 101 og 283 har spørgsmål.");
        Assert.Equal(2, model.Calls);
        var listed = Assert.Single(model.ToolResults);
        Assert.Contains($"101: question: {OlderQuestion}", listed);
        Assert.Contains($"283: question: {Question}", listed);

        voice.Transcriptions.SayAsRecognized("Svar 283 at den skal bruge den eksisterende migration");
        await voice.Events.SaidAsync("Sendt til issue 283.");
        // Each utterance once: a tool round and a respond, no more
        Assert.Equal(4, model.Calls);

        // It reached FakeClaude's stdin through ReplyByVoice, marked as transcribed speech (#460), and the session carried on
        await Eventually.UntilAsync(() => server.StdinOf(asking.Id).Count == 2, () => $"the answer on stdin: {string.Join(" | ", server.StdinOf(asking.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(SpokenInput.Mark(Answer)), server.StdinOf(asking.Id)[1]);
        Assert.Equal(SpokenInput.Mark(Answer), server.InputOf(asking.Id)[^1]);
        Assert.Single(server.StdinOf(older.Id));
        await Eventually.UntilAsync(() => Status(hub, asking.Id) is { State: ProjectState.Idle, LastResult: "Okay." },
            () => $"the session to carry on: {Status(hub, asking.Id)}");
    }

    /// <summary>Issue #460: a reply typed in the app (the hub's ReplyAndResume) reaches the session as it was typed, unmarked.</summary>
    [Fact]
    public async Task A_typed_reply_reaches_the_session_unmarked()
    {
        await using var server = await TestServer.StartAsync(Asking(Question));
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var asking = await CreateAsync(hub, "283-add-migration");
        await WaitForAttentionAsync(hub, asking.Id);

        await hub.InvokeAsync(nameof(IProjectHub.ReplyAndResume), asking.Id, Answer);

        await Eventually.UntilAsync(() => server.StdinOf(asking.Id).Count == 2, () => $"the answer on stdin: {string.Join(" | ", server.StdinOf(asking.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(Answer), server.StdinOf(asking.Id)[1]);
        Assert.Equal(Answer, server.InputOf(asking.Id)[^1]);
        Assert.DoesNotContain(server.InputOf(asking.Id), input => input.Contains(SpokenInput.Marker));
    }

    /// <summary>
    /// Issue #353: a session created on the server after voice started (from the app, here from another hub
    /// connection) is known to voice from the hub's events, answered by its root, and forgotten when it is deleted.
    /// </summary>
    [Fact]
    public async Task A_session_created_after_voice_started_is_known_answered_and_forgotten_when_deleted()
    {
        await using var server = await TestServer.StartAsync(Asking(Question));
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();

        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ListProjects).Respond("1 projekt: testing.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = TestServer.Root, [VoiceTools.TextParameter] = Answer }).Respond("Sendt til testing.")
            .CallTool(VoiceTools.ListProjects).Respond("Ingen projekter.");
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("Klar.");
        Assert.Empty(voice.Session.Handles.All);

        var created = await CreateAsync(hub, "testing");
        await WaitForAttentionAsync(hub, created.Id);
        // From ProjectCreated, not the attention list, which gives the question a handle too
        await Eventually.UntilAsync(() => voice.Session.Projects.Find(new ProjectRef("local", created.Id)) is not null,
            () => $"voice to know testing: {string.Join(", ", voice.Session.Projects.Projects.Select(p => p.Project.Name))}");

        voice.Transcriptions.AddFinal("Hvilke projekter er der?");
        await voice.Events.SaidAsync("1 projekt: testing.");
        var listed = Assert.Single(model.ToolResults);
        Assert.StartsWith("1 project, all in one group:\nProfile ", listed);
        Assert.Contains($", root {TestServer.Root} (1 project):\n- ", listed);
        Assert.Contains("testing (testing", listed);

        voice.Transcriptions.AddFinal($"Svar {TestServer.Root} at den skal bruge den eksisterende migration");
        await voice.Events.SaidAsync("Sendt til create testing.");
        await Eventually.UntilAsync(() => server.StdinOf(created.Id).Count == 2, () => $"the answer on stdin: {string.Join(" | ", server.StdinOf(created.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(SpokenInput.Mark(Answer)), server.StdinOf(created.Id)[1]);

        await hub.InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.DeleteProject), created.Id, true);
        await Eventually.UntilAsync(() => voice.Session.Projects.Projects.Count == 0 && voice.Session.Handles.All.Count == 0,
            () => $"voice to forget it: {string.Join(", ", voice.Session.Handles.All)}");
        voice.Transcriptions.AddFinal("Hvilke projekter er der?");
        await voice.Events.SaidAsync("Ingen projekter.");
        Assert.Equal("No projects on any server.", model.ToolResults[^1]);
    }

    /// <summary>Issue #354: a session started by voice is read back, created on the server on the yes, as the app creates it, and named by its handle.</summary>
    [Fact]
    public async Task A_session_started_by_voice_is_created_on_yes_and_announced_by_its_handle()
    {
        await using var server = await TestServer.StartAsync(Asking(Question));
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, new()
            {
                [VoiceTools.RootParameter] = TestServer.Root,
                [VoiceTools.NameParameter] = "backup job",
                [VoiceTools.PromptParameter] = "Find ud af hvorfor backup-jobbet fejler.",
            })
            .Respond("Ok.");
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start en chat i voice om hvorfor backup-jobbet fejler");
        await voice.Events.SaidAsync($"Skal jeg oprette backup job i {TestServer.Root}, profil {TestServer.Profile}, som Create, med beskrivelsen \"Find ud af hvorfor backup-jobbet fejler\"?");
        Assert.Contains($"in {TestServer.Root} (profile {TestServer.Profile}), action Create", Assert.Single(model.ToolResults));

        voice.Transcriptions.SayAsRecognized("Ja");
        // With the question the session asks at once, when both come before a pause
        await Eventually.UntilAsync(() => voice.Events.Responses.Any(r => r.Contains("backup er oprettet")),
            () => $"the bot to say backup is created; it said: {string.Join(" | ", voice.Events.Responses)}");

        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var created = Assert.Single(await hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)));
        Assert.Equal("backup job", created.Name);
        Assert.Equal(new ProjectRef("local", created.Id), voice.Session.Handles.Resolve("backup"));
        // Its prompt was its first message
        await Eventually.UntilAsync(() => server.StdinOf(created.Id).Count == 1, () => $"the prompt on stdin\n{server.Output}");
        Assert.Contains("backup-jobbet", server.StdinOf(created.Id)[0]);
    }

    /// <summary>
    /// Issue #378: a project that is idle and seen, needing nothing, has its last reply read through the hub's
    /// GetLastReplies: its last turn's, not an earlier one's.
    /// </summary>
    [Fact]
    public async Task The_last_reply_of_a_seen_idle_project_is_read_through_the_hub()
    {
        const string First = "Jeg merger de tre PR'er nu.";
        const string Last = "Alle tre PR'er er merged, og master bygger grønt.";
        await using var server = await TestServer.StartAsync(new FakeScript().EmitInit().Turn(First).Turn(Last).AwaitStdin());
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var project = await CreateAsync(hub, "283-merge");
        await Eventually.UntilAsync(() => Status(hub, project.Id) is { State: ProjectState.Idle, LastResultAt: not null }, () => $"the first turn: {Status(hub, project.Id)}");
        var firstAt = Status(hub, project.Id).LastResultAt;
        await hub.InvokeAsync(nameof(IProjectHub.SendInput), project.Id, "Merge dem");
        await Eventually.UntilAsync(() => Status(hub, project.Id) is { State: ProjectState.Idle } status && status.LastResultAt > firstAt,
            () => $"the last turn: {Status(hub, project.Id)}");
        await hub.InvokeAsync(nameof(IProjectHub.MarkSeen), project.Id);
        Assert.Empty(await hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention)));

        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "283" }).Respond("283: alle tre er merged.");
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Læs hele 283s svar.");
        await voice.Events.SaidAsync("283: alle tre er merged.");

        Assert.EndsWith($"Idle. Last reply: {Last}", Assert.Single(model.ToolResults));
    }

    private static async Task<ProjectStatus> CreateAsync(HubConnection hub, string name) =>
        (await hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), TestServer.Profile, TestServer.Root, null,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["prompt"] = JsonSerializer.SerializeToElement("Ship the issue"),
            })).Project!;

    private static Task WaitForAttentionAsync(HubConnection hub, string projectId) =>
        Eventually.UntilAsync(
            () => hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention)).Result.Any(i => i.ProjectId == projectId),
            () => $"{projectId} to need the user");

    private static ProjectStatus Status(HubConnection hub, string projectId) =>
        hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), projectId).Result;
}
