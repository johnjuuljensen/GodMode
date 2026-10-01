using System.Net;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodMode.Server.Tests;

/// <summary>
/// A real server over one root, whose sessions play the script in the fake, for the fleet's endpoint: an action
/// that starts a session (<see cref="WorkAction"/>), one that starts none (<see cref="ProvisionAction"/>), one whose
/// sessions have the fleet's tools (<see cref="OverseerAction"/>, <c>"fleetTools": true</c>) and one whose sessions
/// have them when granted (<see cref="GrantableAction"/>, <c>"fleetTools": "grantable"</c>).
/// </summary>
internal sealed class FleetRun : IAsyncDisposable
{
    public const string Profile = "fleet";
    public const string RootName = "work";
    public const string WorkAction = "work";
    public const string ProvisionAction = "provision";
    public const string OverseerAction = "overseer";
    public const string GrantableAction = "grantable";

    public static readonly string[] FleetToolNames = ["list_roots", "list_sessions", "read", "resume", "send", "start_session", "stop"];

    private string _workDir = "";

    public ServerProcess Server { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";
    public HttpClient Http { get; private set; } = null!;
    public ServerHubClient Client { get; private set; } = null!;

    public static async Task<FleetRun> StartAsync(FakeScript script)
    {
        var run = new FleetRun { _workDir = ServerProcess.CreateWorkDir("fleet") };
        var scriptPath = Path.Combine(run._workDir, "fake-claude.script");
        script.Save(scriptPath);
        var rootConfig = Path.Combine(run._workDir, "roots", RootName, ".godmode-root");
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
        File.WriteAllText(Path.Combine(rootConfig, $"config.{OverseerAction}.json"), """{ "fleetTools": true }""");
        File.WriteAllText(Path.Combine(rootConfig, $"config.{GrantableAction}.json"), """{ "fleetTools": "grantable" }""");
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

    /// <summary>The root's folder.</summary>
    public string RootPath => Path.Combine(_workDir, "roots", RootName);

    /// <summary>Writes the action's overlay, <c>config.{action}.json</c>, as the host would.</summary>
    public void WriteActionConfig(string action, string json) =>
        File.WriteAllText(Path.Combine(RootPath, ".godmode-root", $"config.{action}.json"), json);

    /// <summary>An MCP client on <paramref name="entry"/>'s endpoint with its headers, as a session's claude calls it.</summary>
    public static async Task<McpClient> ConnectAsync(GodModeMcpEntry entry) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entry.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = entry.Headers.ToDictionary(),
        }));

    /// <summary>The status of an MCP <c>initialize</c> to <paramref name="path"/> with the credential, and the project named: whether the caller got in.</summary>
    public async Task<HttpStatusCode> InitializeAsync(string path, string? token, string? projectId = null)
    {
        using var request = McpEndpointTests.Initialize(projectId, token);
        request.RequestUri = new Uri(path, UriKind.Relative);
        using var response = await Http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>The session's state folder, <c>.godmode/sessions/{id}/</c>, which the session can write.</summary>
    public string StatePath(string projectId) =>
        GodMode.ProjectFiles.SessionState.PathOf(ServerProcess.WorkingFolderOf(RootPath, projectId), projectId.Split('/')[^1]);

    /// <summary>Starts a session of <paramref name="action"/> as the app's create does, over the hub, and returns its ID.</summary>
    public async Task<string> CreateOverHubAsync(string name, string action) =>
        (await Client.Hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), Profile, RootName, action,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["prompt"] = JsonSerializer.SerializeToElement("Start"),
            })).Project!.Id;

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
        (await StartSessionAsync(fleet, name, WorkAction)).GetProperty("Id").GetString()!;

    /// <summary>start_session of <paramref name="action"/>, prompted, with any more of its arguments; fails the test when it refused.</summary>
    public Task<JsonElement> StartSessionAsync(McpClient fleet, string name, string action, Dictionary<string, object?>? more = null) =>
        CallAsync(fleet, "start_session", StartArguments(name, action, more));

    public static Dictionary<string, object?> StartArguments(string name, string action, Dictionary<string, object?>? more = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["profile"] = Profile, ["root"] = RootName, ["action"] = action,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = name, ["prompt"] = "Start" },
        };
        foreach (var (key, value) in more ?? []) arguments[key] = value;
        return arguments;
    }

    public Task<string> CreateOverHubAsync(string name) => CreateOverHubAsync(name, WorkAction);

    /// <summary>Waits until the session's first launch (or its <paramref name="index"/>th) satisfies <paramref name="condition"/>.</summary>
    public async Task<FakeLaunch> WaitForLaunchAsync(string projectId, Func<FakeLaunch, bool> condition, int index = 0)
    {
        var record = Path.Combine(ServerProcess.WorkingFolderOf(RootPath, projectId), "fake-claude.jsonl");
        FakeLaunch? launch = null;
        Assert.True(await LifecycleHarness.WaitForAsync(() =>
                Task.FromResult((launch = FakeRecording.Read(record).ElementAtOrDefault(index)) is { } l && condition(l))),
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
