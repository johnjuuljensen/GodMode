using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Auth;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// GodMode's MCP endpoint against the real server process, called by the fake claude as claude
/// calls it (an MCP client, from the entry the server wrote into its <c>--mcp-config</c>): whose
/// token opens it, what it reports while a prompt waits, and what a cancelled call leaves behind.
/// <see cref="AuthTests"/> has it refuse the user's API key.
/// </summary>
public class McpEndpointTests
{
    private const string Profile = "mcp";
    private const string Root = "lifecycle";

    [Fact]
    public async Task AProjectsOwnToken_OpensTheEndpoint_AnotherProjectsTokenDoesNot()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        var first = await run.CreateAsync("p1");
        var second = await run.CreateAsync("p2");
        var firstEntry = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(first, launch => launch.Stdin.Count > 0));
        var secondEntry = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(second, launch => launch.Stdin.Count > 0));
        Assert.Equal(run.BaseUrl + McpEndpointUrl.Path, firstEntry.Url);
        Assert.Equal(first, firstEntry.ProjectId);

        using var own = await run.Http.SendAsync(Initialize(first, firstEntry.Token));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // A token opens the endpoint for the project it was issued to, whichever project the call names
        using var othersToken = await run.Http.SendAsync(Initialize(first, secondEntry.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, othersToken.StatusCode);
        using var othersProject = await run.Http.SendAsync(Initialize(second, firstEntry.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, othersProject.StatusCode);
    }

    /// <summary>claude gives up on a tool call that sends nothing for 300 seconds: a waiting prompt reports progress.</summary>
    [Fact]
    public async Task AWaitingPrompt_ReportsProgress_UntilItIsAnswered()
    {
        await using var run = await Run.StartAsync(
            new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "ls" }, "toolu_ls").AwaitStdin(),
            keepAliveSeconds: 0.05);
        var id = await run.CreateAsync("p1");
        var asking = await run.Client.WaitForAsync(id, s => s.PendingPermission != null, run.Server);

        await run.WaitForLaunchAsync(id, launch => launch.Progress.Count >= 3);
        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), id, asking.PendingPermission!.RequestId,
            new PermissionDecision(true));

        var answered = await run.WaitForLaunchAsync(id, launch => launch.Permissions.Count == 1);
        Assert.Equal("allow", JsonDocument.Parse(answered.Permissions[0]).RootElement.GetProperty("behavior").GetString());
        Assert.All(answered.Progress, message => Assert.Equal("Waiting for the user", message));
    }

    /// <summary>claude cancelled the call: the prompt is withdrawn, the project runs on, and a message reaches claude again.</summary>
    [Fact]
    public async Task ACancelledCall_WithdrawsTheRequest_AndTheNextInputReachesClaude()
    {
        await using var run = await Run.StartAsync(
            new FakeScript().EmitInit().AwaitStdin().AskPermissionAndCancel("Bash", new { command = "ls" }, "toolu_ls").AwaitStdin(),
            keepAliveSeconds: 0.05);
        var id = await run.CreateAsync("p1");
        await run.Client.WaitForAsync(id, s => s.PendingPermission != null, run.Server);

        var cancelled = await run.WaitForLaunchAsync(id, launch => launch.Permissions.Count == 1);
        Assert.Equal("cancelled", cancelled.Permissions[0]);
        var withdrawn = await run.Client.WaitForAsync(id, s => s.PendingPermission == null && s.State == ProjectState.Running, run.Server);
        Assert.Null(withdrawn.PendingQuestion);

        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.SendInput), id, "Carry on without it");
        var carriedOn = await run.WaitForLaunchAsync(id, launch => launch.Stdin.Count == 2);
        Assert.Contains("Carry on without it", carriedOn.Stdin[1]);
    }

    /// <summary>
    /// A first request to the endpoint, as an MCP client makes it: <c>initialize</c>, which a
    /// stateless server answers on its own. Its status says whether the caller got in.
    /// </summary>
    internal static HttpRequestMessage Initialize(string? projectId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointUrl.Path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"godmode-tests","version":"1.0"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (projectId != null) request.Headers.Add(ProjectTokenAuthenticationHandler.ProjectIdHeader, projectId);
        return request;
    }

    /// <summary>A server over one root whose projects play <paramref name="script"/> in the fake, and a hub client on it.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private string _workDir = "";

        public ServerProcess Server { get; private set; } = null!;
        public string BaseUrl { get; private set; } = "";
        public HttpClient Http { get; private set; } = null!;
        public ServerHubClient Client { get; private set; } = null!;

        public static async Task<Run> StartAsync(FakeScript script, double? keepAliveSeconds = null)
        {
            var run = new Run { _workDir = ServerProcess.CreateWorkDir("mcp") };
            var scriptPath = Path.Combine(run._workDir, "fake-claude.script");
            script.Save(scriptPath);
            var rootConfig = Path.Combine(run._workDir, "roots", Root, ".godmode-root");
            Directory.CreateDirectory(rootConfig);
            File.WriteAllText(Path.Combine(rootConfig, "config.json"), JsonSerializer.Serialize(new
            {
                profileName = Profile,
                environment = new Dictionary<string, string>
                {
                    [FakeClaudeEnvironment.Script] = scriptPath,
                    [FakeClaudeEnvironment.Record] = "fake-claude.jsonl",
                },
            }));

            var environment = new Dictionary<string, string> { ["Claude__Executable"] = LifecycleHarness.FakeClaudePath };
            if (keepAliveSeconds is { } seconds)
                environment[PermissionPromptTool.KeepAliveSetting] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            run.BaseUrl = $"http://127.0.0.1:{ServerProcess.GetFreePort()}";
            run.Server = ServerProcess.Start(run._workDir, run.BaseUrl, environment: environment);
            run.Http = new HttpClient { BaseAddress = new Uri(run.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            await run.Server.WaitForHealthyAsync(run.Http);
            run.Client = new ServerHubClient(run.BaseUrl);
            await run.Client.StartAsync();
            return run;
        }

        public async Task<string> CreateAsync(string name)
        {
            var created = await Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.CreateProject), Profile, Root, null,
                new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement(name),
                    ["prompt"] = JsonSerializer.SerializeToElement("Start"),
                });
            return created.Id;
        }

        /// <summary>Waits until the project's first launch satisfies <paramref name="condition"/>.</summary>
        public async Task<FakeLaunch> WaitForLaunchAsync(string projectId, Func<FakeLaunch, bool> condition)
        {
            var record = Path.Combine(_workDir, "roots", Root, projectId.Split('/')[^1], "fake-claude.jsonl");
            FakeLaunch? launch = null;
            Assert.True(await LifecycleHarness.WaitForAsync(() =>
                    Task.FromResult((launch = FakeRecording.Read(record).FirstOrDefault()) is { } l && condition(l))),
                $"the fake of {projectId} did not get there: {(launch == null ? "no launch" : $"{launch.Stdin.Count} stdin lines, " +
                    $"{launch.Progress.Count} progress, permissions [{string.Join(", ", launch.Permissions)}]")}.\n{Server.Output}");
            return launch!;
        }

        public async ValueTask DisposeAsync()
        {
            if (Client != null) await Client.DisposeAsync();
            Http?.Dispose();
            Server?.Dispose();
            ServerProcess.DeleteWorkDir(_workDir);
        }
    }
}
