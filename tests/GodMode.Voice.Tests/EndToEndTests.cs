using System.Text.Json;
using GodMode.ClientBase.Hub;
using GodMode.FakeClaude;
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

        var model = new ScriptedModel()
            .CallTool(VoiceTools.WhatNeedsMe).Respond("2 venter: 101 og 283 har spørgsmål.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = Answer }).Respond("Sendt til 283.");
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("101 har et spørgsmål.");

        // A project asks while voice is on: the bot names it
        server.UseScript(Asking(Question));
        var asking = await CreateAsync(hub, "283-add-migration");
        await voice.Events.SaidAsync("283 har et spørgsmål.");

        voice.Transcriptions.Say("Hvad venter på mig?");
        await voice.Events.SaidAsync("2 venter: 101 og 283 har spørgsmål.");
        var listed = Assert.Single(model.ToolResults);
        Assert.Contains($"101: question: {OlderQuestion}", listed);
        Assert.Contains($"283: question: {Question}", listed);

        voice.Transcriptions.Say("Svar at den skal bruge den eksisterende migration");
        await voice.Events.SaidAsync("Sendt til 283.");

        // It reached FakeClaude's stdin through ReplyAndResume, and the session carried on
        await Eventually.UntilAsync(() => server.StdinOf(asking.Id).Count == 2, () => $"the answer on stdin: {string.Join(" | ", server.StdinOf(asking.Id))}\n{server.Output}");
        Assert.Contains(JsonSerializer.Serialize(Answer), server.StdinOf(asking.Id)[1]);
        Assert.Single(server.StdinOf(older.Id));
        await Eventually.UntilAsync(() => Status(hub, asking.Id) is { State: ProjectState.Idle, LastResult: "Okay." },
            () => $"the session to carry on: {Status(hub, asking.Id)}");
    }

    private static async Task<ProjectStatus> CreateAsync(HubConnection hub, string name) =>
        await hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.CreateProject), TestServer.Profile, TestServer.Root, null,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["prompt"] = JsonSerializer.SerializeToElement("Ship the issue"),
            });

    private static Task WaitForAttentionAsync(HubConnection hub, string projectId) =>
        Eventually.UntilAsync(
            () => hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention)).Result.Any(i => i.ProjectId == projectId),
            () => $"{projectId} to need the user");

    private static ProjectStatus Status(HubConnection hub, string projectId) =>
        hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), projectId).Result;
}
