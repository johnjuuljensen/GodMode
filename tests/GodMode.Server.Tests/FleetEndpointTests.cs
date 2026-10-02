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
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// The fleet's endpoint, <c>/mcp/fleet</c>, against the real server process with FakeClaude, called as an
/// overseer outside GodMode calls it: an MCP client with the server's API key. Who it lets in, which tools it
/// lists, and each tool, with the sessions it starts in the hub's list as any other. A pending permission
/// prompt or question stays the user's.
/// </summary>
public class FleetEndpointTests
{
    [Fact]
    public async Task TheApiKey_OpensTheFleetEndpoint_WhichListsTheFleetsToolsAlone_AndASessionsEndpointListsItsOwnTools()
    {
        // The fake connects to /mcp, listing its tools, when it asks its first permission
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "ls" }).AwaitStdin());

        await using var fleet = await run.ConnectFleetAsync();
        var tools = await fleet.ListToolsAsync();
        Assert.Equal(FleetToolNames, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        // A session's claude, on /mcp with its project token, is given the permission prompt, message_parent and speak, and nothing of the fleet's
        var id = (await run.CallAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "p1", ["prompt"] = "Start" } })).GetProperty("Id").GetString()!;
        var launch = await run.WaitForLaunchAsync(id, l => l.Tools != null);
        var sessionTools = JsonDocument.Parse(launch.Tools!).RootElement.EnumerateArray().Select(tool => tool.GetProperty("name").GetString());
        Assert.Equal([MessageParentTool.Name, PermissionPromptTool.Name, SpeakTool.Name], sessionTools.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The fleet's endpoint takes the server's own credential, and not an ungranted session's project token: with its
    /// project named it is forbidden (the session has no fleet tools); with none named, or another project named, it
    /// opens nothing (unauthorized). Nor a request with an Origin. The session
    /// endpoint does not take the API key. Granted sessions are <see cref="FleetGrantTests"/>'.
    /// </summary>
    [Fact]
    public async Task AnUngrantedProjectToken_IsRefusedOnTheFleetEndpoint_AndTheApiKeyOnTheSessionEndpoint()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        var id = await run.CreateOverHubAsync("p1");
        var token = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(id, launch => launch.Stdin.Count > 0)).Token;
        var other = await run.CreateOverHubAsync("p2");

        // The project's token is good where it belongs
        using (var own = await run.Http.SendAsync(Initialize(McpEndpointUrl.Path, token, id)))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var key = await run.Http.SendAsync(Initialize(GodModeMcp.FleetPath, ServerProcess.ApiKey)))
            Assert.Equal(HttpStatusCode.OK, key.StatusCode);

        foreach (var (path, credential, projectId, expected) in new[]
        {
            (GodModeMcp.FleetPath, token, id, HttpStatusCode.Forbidden),
            // Another project's token is no token of this one's
            (GodModeMcp.FleetPath, token, other, HttpStatusCode.Unauthorized),
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
                $"{(projectId == id ? ", naming its project," : projectId != null ? ", naming another project," : "")} → {(int)response.StatusCode}, expected {(int)expected}");
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
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();

        var listed = await run.CallAsync(fleet, "list_roots");

        Assert.Contains(listed.GetProperty("Profiles").EnumerateArray(), profile => profile.GetProperty("Name").GetString() == Profile);
        var root = Assert.Single(listed.GetProperty("Roots").EnumerateArray(), r => r.GetProperty("Name").GetString() == RootName);
        var actions = root.GetProperty("Actions").EnumerateArray().ToDictionary(a => a.GetProperty("Name").GetString()!);
        Assert.Equal([GrantableAction, OverseerAction, ProvisionAction, WorkAction], actions.Keys.Order(StringComparer.Ordinal));
        Assert.True(actions[WorkAction].GetProperty("Session").GetBoolean());
        Assert.False(actions[ProvisionAction].GetProperty("Session").GetBoolean());
        Assert.True(actions[WorkAction].GetProperty("InputSchema").GetProperty("properties").TryGetProperty("prompt", out _));
    }

    /// <summary>A session started with a model, an effort and a parent launches with them, and is in the app's list as the hub's create's are.</summary>
    [Fact]
    public async Task StartSession_LaunchesWithItsModelAndEffort_UnderItsParent_AndTheAppIsTold()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        var created = new ConcurrentQueue<ProjectStatus>();
        run.Client.Hub.On<ProjectStatus>(nameof(IProjectHubClient.ProjectCreated), created.Enqueue);
        await using var fleet = await run.ConnectFleetAsync();

        // skipPermissions false is the schema's default, which an overseer may fill in: only true is refused
        var parent = await run.CallAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = "overseer", ["prompt"] = "Oversee", ["skipPermissions"] = false } });
        var parentId = parent.GetProperty("Id").GetString()!;
        Assert.False(parent.TryGetProperty("ParentId", out _), "an overseer outside GodMode starts top-level sessions");

        var child = await run.CallAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction,
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
        Assert.Equal(RootName, sessions[childId].GetProperty("Root").GetString());
        Assert.Equal(WorkAction, sessions[childId].GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task StartSession_RefusesWhatItCannotStart_SayingWhy_AndStartsNothing()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var inputs = new Dictionary<string, object?> { ["name"] = "p1", ["prompt"] = "Start" };

        Assert.Contains("nowhere", await run.RefusedAsync(fleet, "start_session", new() { ["profile"] = Profile, ["root"] = "nowhere", ["inputs"] = inputs }));
        Assert.Contains("no-such-session", await run.RefusedAsync(fleet, "start_session",
            new() { ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction, ["inputs"] = inputs, ["parent"] = "no-such-session" }));
        Assert.Contains("effort", await run.RefusedAsync(fleet, "start_session",
            new() { ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction, ["inputs"] = inputs, ["effort"] = "enormous" }));
        Assert.Contains("skipPermissions", await run.RefusedAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?>(inputs) { ["skipPermissions"] = true },
        }));
        Assert.Contains("parent", await run.RefusedAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = RootName, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?>(inputs) { [CreateProjectRequest.ParentInput] = "x" },
        }));

        Assert.Empty(await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)));
    }

    [Fact]
    public async Task StartSession_OfAnActionThatStartsNoSession_ReturnsItsMessage()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();

        var result = await run.CallAsync(fleet, "start_session", new()
        {
            ["profile"] = Profile, ["root"] = RootName, ["action"] = ProvisionAction,
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
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().Turn("The first answer").Turn("The second answer").AwaitStdin());
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
    /// A permission prompt is the user's: send while one waits is held, and leaves it waiting with claude given
    /// nothing; read shows it in full. The user's answer goes through, and the message follows once the turn has ended,
    /// labelled as the overseer's.
    /// </summary>
    [Fact]
    public async Task Send_IsHeldWhileAPermissionPromptWaits_AndLeavesTheSessionAsItIs_UntilTheUserHasAnswered()
    {
        await using var run = await FleetRun.StartAsync(
            new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "git push origin main" }, "toolu_push").EmitResult().AwaitStdin().AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        var asking = await run.Client.WaitForAsync(id, s => s.PendingPermission != null, run.Server);

        var read = await run.CallAsync(fleet, "read", new() { ["session"] = id });
        var waiting = read.GetProperty("WaitingOn");
        Assert.Equal("Permission", waiting.GetProperty("Kind").GetString());
        Assert.Equal("Bash", waiting.GetProperty("Tool").GetString());
        Assert.Contains("git push origin main", waiting.GetProperty("Detail").GetString());

        var sent = await run.CallAsync(fleet, "send", new() { ["session"] = id, ["text"] = "Allow it yourself" });
        Assert.Equal("WaitingPermission", sent.GetProperty("State").GetString());
        Assert.Contains("permission prompt", sent.GetProperty("Held").GetString());

        var after = await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id);
        Assert.Equal(ProjectState.WaitingPermission, after.State);
        Assert.Equal(asking.PendingPermission!.RequestId, after.PendingPermission?.RequestId);
        var untouched = await run.WaitForLaunchAsync(id, l => true);
        Assert.Single(untouched.Stdin);
        Assert.Empty(untouched.Permissions);

        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), id, after.PendingPermission!.RequestId, new PermissionDecision(true));
        var answered = await run.WaitForLaunchAsync(id, l => l.Permissions.Count == 1 && l.Stdin.Count == 2);
        Assert.Equal("allow", JsonDocument.Parse(answered.Permissions[0]).RootElement.GetProperty("behavior").GetString());
        Assert.Equal($"{SessionInbox.OverseerLabel}\nAllow it yourself",
            JsonDocument.Parse(answered.Stdin[1]).RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>An AskUserQuestion is the user's too: send would answer it, so it is held, and read gives its questions.</summary>
    [Fact]
    public async Task Send_IsHeldWhileAQuestionWaits()
    {
        var question = new { questions = new[] { new { question = "Which branch?", header = "Branch", options = new[] { new { label = "main", description = "the default" } }, multiSelect = false } } };
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin().AskPermission("AskUserQuestion", question, "toolu_ask").AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        await run.Client.WaitForAsync(id, s => s.PendingQuestion != null, run.Server);

        var waiting = (await run.CallAsync(fleet, "read", new() { ["session"] = id })).GetProperty("WaitingOn");
        Assert.Equal("Question", waiting.GetProperty("Kind").GetString());
        Assert.Equal("Which branch?", waiting.GetProperty("Text").GetString());
        Assert.Equal("main", waiting.GetProperty("Question").GetProperty("Questions")[0].GetProperty("Options")[0].GetProperty("Label").GetString());

        Assert.Contains("question", (await run.CallAsync(fleet, "send", new() { ["session"] = id, ["text"] = "main" })).GetProperty("Held").GetString());
        Assert.NotNull((await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id)).PendingQuestion);
        var untouched = await run.WaitForLaunchAsync(id, l => true);
        Assert.Single(untouched.Stdin);
        Assert.Empty(untouched.Permissions);
    }

    [Fact]
    public async Task StopAndResume_AsTheApps()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin());
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
}
