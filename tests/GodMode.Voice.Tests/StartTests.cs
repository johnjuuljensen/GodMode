using GodMode.ClientBase.Hub;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.FakeClaude;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;
using System.Text.Json;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #381: the voice log of 2026-10-01, "0 of 1 servers answered within 00:00:03": the session said "Klar" with no
/// projects, and the server answered a second later. A command in that second got "Ukendt".
/// </summary>
public sealed class StartTests
{
    /// <summary>The wait for every server, short here, as the app's 3 s was too short for a server coming up.</summary>
    private static readonly TimeSpan InitialWait = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task A_server_that_answers_after_the_initial_wait_is_heard_before_Klar_and_a_command_then_finds_its_project()
    {
        await using var server = await TestServer.StartAsync(new FakeScript().EmitInit().Turn("Færdig.").AwaitStdin());
        await using var hub = HubConnections.Build(new RelayTarget($"{server.Url}/hubs/projects", TestServer.ApiKey));
        await hub.StartAsync();
        var project = (await hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), TestServer.Profile, TestServer.Root, null,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("283-voice"),
                ["prompt"] = JsonSerializer.SerializeToElement("Ship the issue"),
            })).Project!;

        // The server is reached only after the initial wait, as a connection that comes up late
        var late = new LateDirectory(server.ServerDirectory());
        _ = Task.Delay(InitialWait * 3).ContinueWith(_ => late.Open());
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" }).Respond("283 er i gang.");
        await using var servers = new HubServers(late, NullLoggerFactory.Instance, retryDelay: TimeSpan.FromMilliseconds(100));
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(InitialWait, ct));
        await voice.Events.SaidAsync("Klar.");

        // Right then
        voice.Transcriptions.SayAsRecognized("Status på 283");
        // Its result in the code's words (#456) once its turn has ended, idle since it said no outcome (#467), else the
        // model's line on it while it runs; never "Ukendt"
        await Eventually.UntilAsync(() => voice.Events.Responses.Any(r => r is "issue 283 er idle: done." or "283 er i gang."),
            () => $"the status of 283; it said: {string.Join(" | ", voice.Events.Responses)}");
        Assert.DoesNotContain(model.ToolResults, r => r.Contains("Unknown project", StringComparison.Ordinal));
        Assert.NotNull(voice.Session.Handles.Resolve("283"));
        Assert.Equal(new ProjectRef("local", project.Id), voice.Session.Handles.Resolve("283"));
    }

    /// <summary>
    /// A server that never answers holds the start only for the first-answer wait: the session then starts, and its
    /// greeting says no server answers, so "Ukendt" after it is not taken for a project that is not there.
    /// </summary>
    [Fact]
    public async Task A_server_that_never_answers_holds_the_start_for_the_first_answer_wait_only_and_the_greeting_says_so()
    {
        var firstAnswerWait = TimeSpan.FromSeconds(1);
        var never = new LateDirectory(TestServer.Unreachable("local"));
        var model = new ScriptedChatClient();
        await using var servers = new HubServers(never, NullLoggerFactory.Instance, retryDelay: TimeSpan.FromMilliseconds(100),
            firstAnswerWait: firstAnswerWait);
        var started = DateTime.UtcNow;
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: ct => servers.ConnectAsync(InitialWait, ct));
        var took = DateTime.UtcNow - started;

        await voice.Events.SaidAsync("Klar. Ingen server svarer endnu.");
        Assert.InRange(took, firstAnswerWait - TimeSpan.FromMilliseconds(100), firstAnswerWait + TimeSpan.FromSeconds(5));
        Assert.Empty(voice.Session.Projects.Projects);
    }

    /// <summary>With no servers, or some answering, the greeting is "Klar." alone.</summary>
    [Fact]
    public void The_greeting_says_no_server_answers_only_when_there_are_servers_and_none_did()
    {
        var phrases = new VoicePhrases(VoiceSettings.Default.Languages);
        Assert.Equal("Klar.", phrases.Greeting(new ServersHeard(0, 0)));
        Assert.Equal("Klar.", phrases.Greeting(new ServersHeard(1, 2)));
        Assert.Equal("Klar. Ingen server svarer endnu.", phrases.Greeting(new ServersHeard(0, 1)));
    }

    /// <summary>A directory whose server cannot be reached until <see cref="Open"/>: its connection comes up then.</summary>
    internal sealed class LateDirectory(IServerDirectory inner) : IServerDirectory
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default) => inner.ListAllServersAsync(ct);
        public Task<IReadOnlyList<RegistrationListing>> ListByRegistrationAsync(CancellationToken ct = default) => inner.ListByRegistrationAsync(ct);

        public async Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default)
        {
            await _open.Task.WaitAsync(ct);
            return await inner.ResolveAsync(serverId, ct);
        }

        public Task<bool> StartServerAsync(string serverId) => inner.StartServerAsync(serverId);
        public Task<bool> StopServerAsync(string serverId) => inner.StopServerAsync(serverId);
        public Task<bool> RemoveServerAsync(string serverId) => inner.RemoveServerAsync(serverId);
    }
}
