using System.Collections.Concurrent;
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
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodMode.Server.Tests;

/// <summary>
/// The fleet's endpoint, <c>/mcp/fleet</c>, against the real server process with FakeClaude, called as an
/// overseer outside GodMode calls it: an MCP client with the server's API key. Who it lets in, which tools it
/// lists, and each tool, with the sessions it starts in the hub's list as any other. A pending permission
/// prompt or question stays the user's.
/// </summary>
public class FleetEndpointTests
{
    private const string Profile = "fleet";
    private const string Root = "work";
    private const string WorkAction = "work";
    private const string ProvisionAction = "provision";

    private static readonly string[] FleetToolNames = ["list_roots", "list_sessions", "read", "resume", "send", "start_session", "stop"];

    [Fact]
    public async Task TheApiKey_OpensTheFleetEndpoint_WhichListsTheFleetsToolsAlone_AndASessionsEndpointListsItsPromptAlone()
    {
        // The fake connects to /mcp, listing its tools, when it asks its first permission
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "ls" }).AwaitStdin());

        await using var fleet = await run.ConnectFleetAsync();
        var tools = await fleet.ListToolsAsync();
        Assert.Equal(FleetToolNames, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        // A session's claude, on /mcp with its project token, is given the permission prompt and nothing of the fleet's
        var id = (await run.CallAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "p1", ["prompt"] = "Start" } })).GetProperty("Id").GetString()!;
        var launch = await run.WaitForLaunchAsync(id, l => l.Tools != null);
        var sessionTools = JsonDocument.Parse(launch.Tools!).RootElement.EnumerateArray().Select(tool => tool.GetProperty("name").GetString());
        Assert.Equal([PermissionPromptTool.Name], sessionTools);
    }

    /// <summary>
    /// The fleet's endpoint takes the server's own credential alone: not a project token, with its project named
    /// or not, and not a request with an Origin. The session endpoint does not take the API key.
    /// </summary>
    [Fact]
    public async Task AProjectToken_IsRefusedOnTheFleetEndpoint_AndTheApiKeyOnTheSessionEndpoint()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        var id = await run.CreateOverHubAsync("p1");
        var token = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(id, launch => launch.Stdin.Count > 0)).Token;

        // The project's token is good where it belongs
        using (var own = await run.Http.SendAsync(Initialize(McpEndpointUrl.Path, token, id)))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var key = await run.Http.SendAsync(Initialize(GodModeMcp.FleetPath, ServerProcess.ApiKey)))
            Assert.Equal(HttpStatusCode.OK, key.StatusCode);

        foreach (var (path, credential, projectId, expected) in new[]
        {
            (GodModeMcp.FleetPath, token, id, HttpStatusCode.Unauthorized),
            (GodModeMcp.FleetPath, token, null, HttpStatusCode.Unauthorized),
            (GodModeMcp.FleetPath, null, null, HttpStatusCode.Unauthorized),
            (GodModeMcp.FleetPath, "not-the-key", null, HttpStatusCode.Unauthorized),
            (McpEndpointUrl.Path, ServerProcess.ApiKey, null, HttpStatusCode.Unauthorized),
            (McpEndpointUrl.Path, ServerProcess.ApiKey, id, HttpStatusCode.Unauthorized),
        })
        {
            using var response = await run.Http.SendAsync(Initialize(path, credential, projectId));
            Assert.True(response.StatusCode == expected,
                $"POST {path} with {(credential == token ? "the project token" : credential == ServerProcess.ApiKey ? "the API key" : credential ?? "nothing")}" +
                $"{(projectId != null ? ", naming its project," : "")} → {(int)response.StatusCode}, expected {(int)expected}");
        }

        // No browser is a client, the key or not
        using var withOrigin = Initialize(GodModeMcp.FleetPath, ServerProcess.ApiKey);
        withOrigin.Headers.Add("Origin", run.BaseUrl);
        using var refused = await run.Http.SendAsync(withOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task ListRoots_GivesTheActionsAndTheirSchemas()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();

        var listed = await run.CallAsync(fleet, "list_roots");

        Assert.Contains(listed.GetProperty("Profiles").EnumerateArray(), profile => profile.GetProperty("Name").GetString() == Profile);
        var root = Assert.Single(listed.GetProperty("Roots").EnumerateArray(), r => r.GetProperty("Name").GetString() == Root);
        var actions = root.GetProperty("Actions").EnumerateArray().ToDictionary(a => a.GetProperty("Name").GetString()!);
        Assert.Equal([ProvisionAction, WorkAction], actions.Keys.Order(StringComparer.Ordinal));
        Assert.True(actions[WorkAction].GetProperty("Session").GetBoolean());
        Assert.False(actions[ProvisionAction].GetProperty("Session").GetBoolean());
        Assert.True(actions[WorkAction].GetProperty("InputSchema").GetProperty("properties").TryGetProperty("prompt", out _));
    }

    /// <summary>A session started with a model, an effort and a parent launches with them, and is in the app's list as the hub's create's are.</summary>
    [Fact]
    public async Task StartSession_LaunchesWithItsModelAndEffort_UnderItsParent_AndTheAppIsTold()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        var created = new ConcurrentQueue<ProjectStatus>();
        run.Client.Hub.On<ProjectStatus>(nameof(IProjectHubClient.ProjectCreated), created.Enqueue);
        await using var fleet = await run.ConnectFleetAsync();

        var parent = await run.CallAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "overseer", ["prompt"] = "Oversee" } });
        var parentId = parent.GetProperty("Id").GetString()!;
        Assert.False(parent.TryGetProperty("ParentId", out _), "an overseer outside GodMode starts top-level sessions");

        var child = await run.CallAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "worker", ["prompt"] = "Work" },
            ["model"] = "sonnet", ["effort"] = "high", ["parent"] = parentId,
        });
        var childId = child.GetProperty("Id").GetString()!;
        Assert.Equal(parentId, child.GetProperty("ParentId").GetString());
        Assert.Equal("sonnet", child.GetProperty("Model").GetString());
        Assert.Equal("high", child.GetProperty("Effort").GetString());

        var launch = await run.WaitForLaunchAsync(childId, l => l.Stdin.Count > 0);
        Assert.Equal("sonnet", launch.ArgValue("--model"));
        Assert.Equal("high", launch.ArgValue("--effort"));
        Assert.Contains("Work", launch.Stdin[0]);

        // In the app's list, under its parent, as the hub's own create pushes it
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(created.Any(s => s.Id == childId))), "ProjectCreated was not pushed");
        var summaries = await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects));
        Assert.Equal(parentId, Assert.Single(summaries, s => s.Id == childId).ParentId);

        var sessions = (await run.CallAsync(fleet, "list_sessions")).EnumerateArray().ToDictionary(s => s.GetProperty("Id").GetString()!);
        Assert.Equal(new[] { childId, parentId }.Order(StringComparer.Ordinal), sessions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(parentId, sessions[childId].GetProperty("ParentId").GetString());
        Assert.Equal(Profile, sessions[childId].GetProperty("Profile").GetString());
        Assert.Equal(Root, sessions[childId].GetProperty("Root").GetString());
        Assert.Equal(WorkAction, sessions[childId].GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task StartSession_RefusesWhatItCannotStart_SayingWhy_AndStartsNothing()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var inputs = new Dictionary<string, object?> { ["name"] = "p1", ["prompt"] = "Start" };

        Assert.Contains("nowhere", await run.RefusedAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = "nowhere", ["inputs"] = inputs }));
        Assert.Contains("no-such-session", await run.RefusedAsync(fleet, "start_session",
            new() { ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction, ["inputs"] = inputs, ["parent"] = "no-such-session" }));
        Assert.Contains("effort", await run.RefusedAsync(fleet, "start_session",
            new() { ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction, ["inputs"] = inputs, ["effort"] = "enormous" }));
        Assert.Contains("skipPermissions", await run.RefusedAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?>(inputs) { ["skipPermissions"] = true },
        }));
        Assert.Contains("parent", await run.RefusedAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?>(inputs) { [CreateProjectRequest.ParentInput] = "x" },
        }));

        Assert.Empty(await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)));
    }

    [Fact]
    public async Task StartSession_OfAnActionThatStartsNoSession_ReturnsItsMessage()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();

        var result = await run.CallAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = Root, ["action"] = ProvisionAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "fresh" },
        });

        Assert.Equal("Provisioned fresh", result.GetProperty("Message").GetString());
        Assert.False(result.TryGetProperty("Id", out _));
        Assert.Empty(await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)));
    }

    /// <summary>send is a reply, and read gives the session's last replies, oldest first, and what it waits on.</summary>
    [Fact]
    public async Task Send_ReachesClaude_AndRead_GivesTheLastReplies()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().Turn("The first answer").Turn("The second answer").AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle && s.LastResult != null, run.Server);

        var first = await run.CallAsync(fleet, "read", new() { ["session"] = id });
        Assert.Equal("Idle", first.GetProperty("State").GetString());
        var reply = Assert.Single(first.GetProperty("Replies").EnumerateArray());
        Assert.Equal("The first answer", reply.GetProperty("Text").GetString());
        Assert.True(reply.GetProperty("Finished").GetBoolean());
        Assert.Equal("Finished", first.GetProperty("WaitingOn").GetProperty("Kind").GetString());
        var listed = Assert.Single((await run.CallAsync(fleet, "list_sessions")).EnumerateArray());
        Assert.Equal("Finished", listed.GetProperty("Needs").GetString());

        await run.CallAsync(fleet, "send", new() { ["session"] = id, ["text"] = "Go on" });
        var launch = await run.WaitForLaunchAsync(id, l => l.Stdin.Count == 2);
        Assert.Contains("Go on", launch.Stdin[1]);
        Assert.True(await LifecycleHarness.WaitForAsync(async () =>
            (await run.CallAsync(fleet, "read", new() { ["session"] = id, ["turns"] = 2 })).GetProperty("Replies").GetArrayLength() == 2
            && (await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id)).State == ProjectState.Idle));

        var both = await run.CallAsync(fleet, "read", new() { ["session"] = id, ["turns"] = 5 });
        Assert.Equal(["The first answer", "The second answer"],
            both.GetProperty("Replies").EnumerateArray().Select(r => r.GetProperty("Text").GetString()));
        Assert.Contains("turns", await run.RefusedAsync(fleet, "read", new() { ["session"] = id, ["turns"] = 0 }));
        Assert.Contains("nope", await run.RefusedAsync(fleet, "read", new() { ["session"] = "nope" }));
    }

    /// <summary>
    /// A permission prompt is the user's: send is refused while one waits, and leaves it waiting with claude given
    /// nothing; read shows it in full. The user's answer still goes through.
    /// </summary>
    [Fact]
    public async Task Send_IsRefusedWhileAPermissionPromptWaits_AndLeavesTheSessionAsItIs()
    {
        await using var run = await Run.StartAsync(
            new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "git push origin main" }, "toolu_push").AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        var asking = await run.Client.WaitForAsync(id, s => s.PendingPermission != null, run.Server);

        var read = await run.CallAsync(fleet, "read", new() { ["session"] = id });
        var waiting = read.GetProperty("WaitingOn");
        Assert.Equal("Permission", waiting.GetProperty("Kind").GetString());
        Assert.Equal("Bash", waiting.GetProperty("Tool").GetString());
        Assert.Contains("git push origin main", waiting.GetProperty("Detail").GetString());

        Assert.Contains("permission", await run.RefusedAsync(fleet, "send", new() { ["session"] = id, ["text"] = "Allow it yourself" }));

        var after = await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id);
        Assert.Equal(ProjectState.WaitingPermission, after.State);
        Assert.Equal(asking.PendingPermission!.RequestId, after.PendingPermission?.RequestId);
        var untouched = await run.WaitForLaunchAsync(id, l => true);
        Assert.Single(untouched.Stdin);
        Assert.Empty(untouched.Permissions);

        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), id, after.PendingPermission!.RequestId, new PermissionDecision(true));
        var answered = await run.WaitForLaunchAsync(id, l => l.Permissions.Count == 1);
        Assert.Equal("allow", JsonDocument.Parse(answered.Permissions[0]).RootElement.GetProperty("behavior").GetString());
    }

    /// <summary>An AskUserQuestion is the user's too: send would answer it, so it is refused, and read gives its questions.</summary>
    [Fact]
    public async Task Send_IsRefusedWhileAQuestionWaits()
    {
        var question = new { questions = new[] { new { question = "Which branch?", header = "Branch", options = new[] { new { label = "main", description = "the default" } }, multiSelect = false } } };
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin().AskPermission("AskUserQuestion", question, "toolu_ask").AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        await run.Client.WaitForAsync(id, s => s.PendingQuestion != null, run.Server);

        var waiting = (await run.CallAsync(fleet, "read", new() { ["session"] = id })).GetProperty("WaitingOn");
        Assert.Equal("Question", waiting.GetProperty("Kind").GetString());
        Assert.Equal("Which branch?", waiting.GetProperty("Text").GetString());
        Assert.Equal("main", waiting.GetProperty("Question").GetProperty("Questions")[0].GetProperty("Options")[0].GetProperty("Label").GetString());

        Assert.Contains("question", await run.RefusedAsync(fleet, "send", new() { ["session"] = id, ["text"] = "main" }));
        Assert.NotNull((await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id)).PendingQuestion);
        var untouched = await run.WaitForLaunchAsync(id, l => true);
        Assert.Single(untouched.Stdin);
        Assert.Empty(untouched.Permissions);
    }

    [Fact]
    public async Task StopAndResume_AsTheApps()
    {
        await using var run = await Run.StartAsync(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        Assert.True(await LifecycleHarness.WaitForAsync(async () =>
            (await run.CallAsync(fleet, "read", new() { ["session"] = id })).GetProperty("Replies").GetArrayLength() == 1), "it never said anything");

        Assert.Equal("Stopped", (await run.CallAsync(fleet, "stop", new() { ["session"] = id })).GetProperty("State").GetString());
        // What it said before the stop
        var reply = Assert.Single((await run.CallAsync(fleet, "read", new() { ["session"] = id })).GetProperty("Replies").EnumerateArray());
        Assert.Equal("Working on it", reply.GetProperty("Text").GetString());

        Assert.Equal("Idle", (await run.CallAsync(fleet, "resume", new() { ["session"] = id })).GetProperty("State").GetString());
        Assert.Contains("nope", await run.RefusedAsync(fleet, "stop", new() { ["session"] = "nope" }));
    }

    /// <summary>An <c>initialize</c>, as an MCP client's first request, to <paramref name="path"/>: its status says whether the caller got in.</summary>
    private static HttpRequestMessage Initialize(string path, string? token, string? projectId = null)
    {
        var request = McpEndpointTests.Initialize(projectId, token);
        request.RequestUri = new Uri(path, UriKind.Relative);
        return request;
    }

    /// <summary>A server over one root, with an action that starts a session and one that starts none, whose sessions play the script in the fake.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private string _workDir = "";

        public ServerProcess Server { get; private set; } = null!;
        public string BaseUrl { get; private set; } = "";
        public HttpClient Http { get; private set; } = null!;
        public ServerHubClient Client { get; private set; } = null!;

        public static async Task<Run> StartAsync(FakeScript script)
        {
            var run = new Run { _workDir = ServerProcess.CreateWorkDir("fleet") };
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
            File.WriteAllText(Path.Combine(rootConfig, $"config.{WorkAction}.json"), "{}");
            File.WriteAllText(Path.Combine(rootConfig, $"config.{ProvisionAction}.json"), """{ "session": false, "create": "provision.ps1" }""");
            File.WriteAllText(Path.Combine(rootConfig, "provision.ps1"), """
                $ErrorActionPreference = 'Stop'
                Set-Content -Path $env:GODMODE_RESULT_FILE -Value "message=Provisioned $env:GODMODE_INPUT_NAME"
                """);

            run.BaseUrl = $"http://127.0.0.1:{ServerProcess.GetFreePort()}";
            try
            {
                run.Server = ServerProcess.Start(run._workDir, run.BaseUrl,
                    environment: new Dictionary<string, string> { ["Claude__Executable"] = LifecycleHarness.FakeClaudePath });
                run.Http = new HttpClient { BaseAddress = new Uri(run.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
                await run.Server.WaitForHealthyAsync(run.Http);
                run.Client = new ServerHubClient(run.BaseUrl);
                await run.Client.StartAsync();
                return run;
            }
            catch
            {
                // The test never gets the run to dispose: a server left running outlives the test host
                await run.DisposeAsync();
                throw;
            }
        }

        /// <summary>An MCP client on the fleet's endpoint with the server's API key, as an overseer's <c>.mcp.json</c> gives it.</summary>
        public async Task<McpClient> ConnectFleetAsync() =>
            await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(BaseUrl + GodModeMcp.FleetPath),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {ServerProcess.ApiKey}" },
            }));

        /// <summary>The tool's JSON answer; fails the test when the tool refused.</summary>
        public async Task<JsonElement> CallAsync(McpClient fleet, string tool, Dictionary<string, object?>? arguments = null)
        {
            var result = await fleet.CallToolAsync(tool, arguments ?? []);
            var text = Text(result);
            Assert.True(result.IsError != true, $"{tool} refused: {text}\n{Server.Output}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        /// <summary>Why the tool refused; fails the test when it did not.</summary>
        public async Task<string> RefusedAsync(McpClient fleet, string tool, Dictionary<string, object?> arguments)
        {
            var result = await fleet.CallToolAsync(tool, arguments);
            var text = Text(result);
            Assert.True(result.IsError == true, $"{tool} was not refused: {text}");
            return text;
        }

        private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        /// <summary>Starts a session of the work action through the fleet, prompted, and returns its ID.</summary>
        public async Task<string> StartAsync(McpClient fleet, string name) =>
            (await CallAsync(fleet, "start_session", new()
            {
                ["profile"] = Profile, ["root"] = Root, ["action"] = WorkAction,
                ["inputs"] = new Dictionary<string, object?> { ["name"] = name, ["prompt"] = "Start" },
            })).GetProperty("Id").GetString()!;

        public async Task<string> CreateOverHubAsync(string name) =>
            (await Client.Hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), Profile, Root, WorkAction,
                new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement(name),
                    ["prompt"] = JsonSerializer.SerializeToElement("Start"),
                })).Project!.Id;

        /// <summary>Waits until the session's first launch satisfies <paramref name="condition"/>.</summary>
        public async Task<FakeLaunch> WaitForLaunchAsync(string projectId, Func<FakeLaunch, bool> condition)
        {
            var record = Path.Combine(ServerProcess.WorkingFolderOf(Path.Combine(_workDir, "roots", Root), projectId), "fake-claude.jsonl");
            FakeLaunch? launch = null;
            Assert.True(await LifecycleHarness.WaitForAsync(() =>
                    Task.FromResult((launch = FakeRecording.Read(record).FirstOrDefault()) is { } l && condition(l))),
                $"the fake of {projectId} did not get there: {(launch == null ? "no launch" : $"{launch.Stdin.Count} stdin lines, " +
                    $"permissions [{string.Join(", ", launch.Permissions)}]")}.\n{Server.Output}");
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
