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
/// Issue #513 against the real server, whose project runs FakeClaude: the user asks about a project with no recap, voice
/// asks it for one (<c>/recap</c> on its stdin, as it is), says it has, and reads the recap the next time it is asked.
/// The recap's lines are the ones claude 2.1.289 writes headless.
/// </summary>
public sealed class RecapEndToEndTests
{
    private const string RecapText = "Migrationen er lavet, og testene er grønne.";

    private static readonly string RecapResult = JsonSerializer.Serialize(new
    {
        type = "result", subtype = "success", is_error = false, num_turns = 0, result = RecapText,
        session_id = FakeScript.SessionIdPlaceholder, usage = new { input_tokens = 0, output_tokens = 0 },
    });

    [Fact]
    public async Task Voice_asks_a_project_with_no_recap_for_one_once_and_reads_it_the_next_time()
    {
        var script = new FakeScript().EmitInit().Turn("Færdig.")
            .AwaitStdin().EmitAssistant(RecapText).Emit(RecapResult).AwaitStdin();
        await using var server = await TestServer.StartAsync(script);
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var project = (await hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), TestServer.Profile, TestServer.Root, null,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("283-voice"),
                ["prompt"] = JsonSerializer.SerializeToElement("Ship the issue"),
            })).Project!;
        await Eventually.UntilAsync(() => Status(hub, project.Id) is { State: ProjectState.Idle, LastResult: "done" },
            () => $"the first turn to end: {Status(hub, project.Id)}");
        await hub.InvokeAsync(nameof(IProjectHub.MarkSeen), project.Id);

        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" });
        await using var servers = new HubServers(server.ServerDirectory(), NullLoggerFactory.Instance);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(TimeSpan.FromSeconds(20), ct));
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvordan går det med 283?");
        await voice.Events.SaidAsync("Sidste resultat fra issue 283, voice: done. Jeg har bedt den om et resumé, som jeg læser næste gang du spørger.");

        // /recap reached claude as it is, not marked as speech; its answer is the recap, and the last result stays
        await Eventually.UntilAsync(() => Status(hub, project.Id) is { Recap: RecapText, State: ProjectState.Idle },
            () => $"the recap: {Status(hub, project.Id)}\n{string.Join(" | ", server.StdinOf(project.Id))}");
        Assert.Equal("/recap", server.InputOf(project.Id)[^1]);
        Assert.Equal("done", Status(hub, project.Id).LastResult);
        Assert.DoesNotContain(await hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention)), i => i.ProjectId == project.Id);

        voice.Transcriptions.SayAsRecognized("Og 283 nu?");
        await voice.Events.SaidAsync($"issue 283: {RecapText}");
        Assert.Equal(2, server.StdinOf(project.Id).Count);
    }

    private static ProjectStatus Status(HubConnection hub, string projectId) =>
        hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), projectId).Result;
}
