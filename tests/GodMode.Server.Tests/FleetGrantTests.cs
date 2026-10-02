using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Server.Auth;
using GodMode.Server.Services;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// Overseer sessions, against the real server with FakeClaude: a session has the fleet's tools when its action's
/// config says <c>"fleetTools": true</c>, or <c>"grantable"</c> and the session that started it granted them. Its MCP
/// config then lists the fleet's endpoint, which takes its project token and checks the grant, in the root's config
/// as it is then, on every call. Nothing in its working folder grants them. The sessions it starts are its
/// children unless it asks for top-level ones.
/// </summary>
public class FleetGrantTests
{
    /// <summary>A claude that starts, takes its prompt, and waits.</summary>
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin().AwaitStdin();

    private static string? ParentOf(JsonElement started) => started.TryGetProperty("ParentId", out var parent) ? parent.GetString() : null;

    private static string IdOf(JsonElement started) => started.GetProperty("Id").GetString()!;

    /// <summary>An overseer action's session, created in the app: its launch, and the fleet's entry in its MCP config.</summary>
    private static async Task<(string Id, GodModeMcpEntry Fleet)> OverseerAsync(FleetRun run, string name = "overseer")
    {
        var id = await run.CreateOverHubAsync(name, OverseerAction);
        return (id, GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(id, launch => launch.Stdin.Count > 0)));
    }

    /// <summary>The session's MCP config holds GodMode's server alone, and its token is refused on the fleet's endpoint.</summary>
    private static async Task AssertNoFleetToolsAsync(FleetRun run, string id, int launch = 0)
    {
        var entry = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(id, l => l.Stdin.Count > 0, launch));
        Assert.Equal(HttpStatusCode.Forbidden, await run.InitializeAsync(GodModeMcp.FleetPath, entry.Token, id));
    }

    [Fact]
    public async Task ASessionOfAnActionWithFleetTools_IsGivenTheFleetsEndpoint_WhereItsTokenGetsTheFleetsTools_AndOnlyThere()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (_, fleetEntry) = await OverseerAsync(run);

        Assert.Equal(run.BaseUrl + GodModeMcp.FleetPath, fleetEntry.Url);
        await using var fleet = await ConnectAsync(fleetEntry);
        Assert.Equal(FleetToolNames, (await fleet.ListToolsAsync()).Select(tool => tool.Name).Order(StringComparer.Ordinal));

        // Its own endpoint, with the same token, gives it the permission prompt, message_parent and speak, and nothing of the fleet's
        await using var own = await ConnectAsync(fleetEntry with { Url = run.BaseUrl + McpEndpointUrl.Path });
        Assert.Equal([MessageParentTool.Name, PermissionPromptTool.Name, SpeakTool.Name], (await own.ListToolsAsync()).Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>A session of an action without <c>fleetTools</c>: no fleet entry in its MCP config, and its token is forbidden there.</summary>
    [Fact]
    public async Task ASessionWithoutTheGrant_GetsNoFleetToolsInItsConfig_AndItsTokenIsRefused()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var worker = await run.CreateOverHubAsync("worker", WorkAction);
        // A grantable action's session that nobody granted them has none either
        var ungranted = await run.CreateOverHubAsync("ungranted", GrantableAction);

        await AssertNoFleetToolsAsync(run, worker);
        await AssertNoFleetToolsAsync(run, ungranted);
    }

    [Fact]
    public async Task AGrantedSessionsChildren_AreItsOwn_WithNoGrant_UnlessItStartsThemTopLevel_OrNamesAnotherParent()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (overseerId, fleetEntry) = await OverseerAsync(run);
        await using var fleet = await ConnectAsync(fleetEntry);

        var child = await run.StartSessionAsync(fleet, "worker", WorkAction);
        Assert.Equal(overseerId, ParentOf(child));
        await AssertNoFleetToolsAsync(run, IdOf(child));

        Assert.Null(ParentOf(await run.StartSessionAsync(fleet, "peer", WorkAction, new() { ["top_level"] = true })));
        Assert.Equal(IdOf(child), ParentOf(await run.StartSessionAsync(fleet, "grandchild", WorkAction, new() { ["parent"] = IdOf(child) })));
        Assert.Contains("top_level", await run.RefusedAsync(fleet, "start_session",
            StartArguments("both", WorkAction, new() { ["parent"] = IdOf(child), ["top_level"] = true })));

        // In the app's list under it, as the hub has it
        var summaries = await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects));
        Assert.Equal(overseerId, Assert.Single(summaries, s => s.Id == IdOf(child)).ParentId);
    }

    [Fact]
    public async Task AGrantedSession_GrantsAChildOfAGrantableAction_WhichOverseesItsOwn_AndNoChildOfAnActionThatAllowsNoGrant()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (_, fleetEntry) = await OverseerAsync(run);
        await using var fleet = await ConnectAsync(fleetEntry);

        var epic = IdOf(await run.StartSessionAsync(fleet, "epic", GrantableAction, new() { ["fleet_tools"] = true }));
        var epicEntry = GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(epic, launch => launch.Stdin.Count > 0));
        await using var epicFleet = await ConnectAsync(epicEntry);
        Assert.Equal(epic, ParentOf(await run.StartSessionAsync(epicFleet, "worker", WorkAction)));

        // Not granted, the grantable action's session has none
        await AssertNoFleetToolsAsync(run, IdOf(await run.StartSessionAsync(fleet, "plain", GrantableAction)));

        // An action that allows no grant refuses one, and nothing is started
        var before = (await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Length;
        Assert.Contains("grantable", await run.RefusedAsync(fleet, "start_session", StartArguments("w", WorkAction, new() { ["fleet_tools"] = true })));
        Assert.Equal(before, (await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Length);
    }

    /// <summary>
    /// A session without the tools cannot grant them: its token does not reach start_session, so the call that
    /// would start a granted child is refused before any tool runs, and nothing is started.
    /// </summary>
    [Fact]
    public async Task AnUngrantedSession_CannotGrantAChildTheFleetsTools()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var worker = await run.CreateOverHubAsync("worker", WorkAction);
        var token = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(worker, launch => launch.Stdin.Count > 0)).Token;

        using var call = new HttpRequestMessage(HttpMethod.Post, GodModeMcp.FleetPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "start_session", arguments = StartArguments("epic", GrantableAction, new() { ["fleet_tools"] = true }) },
            }), Encoding.UTF8, "application/json"),
        };
        call.Headers.Accept.ParseAdd("application/json");
        call.Headers.Accept.ParseAdd("text/event-stream");
        call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        call.Headers.Add(ProjectTokenAuthenticationHandler.ProjectIdHeader, worker);
        using var refused = await run.Http.SendAsync(call);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal([worker], (await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Select(s => s.Id));
    }

    /// <summary>
    /// What a prompt-injected session could write: its own settings.json, naming the overseer action and asking for
    /// the tools. Its token is still refused, and its next launch is given no fleet entry.
    /// </summary>
    [Fact]
    public async Task ASessionThatGrantsItselfTheFleetsToolsInItsSettings_IsRefused()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        await using var user = await run.ConnectFleetAsync();
        var worker = await run.CreateOverHubAsync("worker", WorkAction);
        await run.WaitForLaunchAsync(worker, launch => launch.Stdin.Count > 0);

        var settingsPath = Path.Combine(run.StatePath(worker), "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["actionName"] = OverseerAction;
        settings["fleetTools"] = true;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        await AssertNoFleetToolsAsync(run, worker);

        await run.CallAsync(user, "stop", new() { ["session"] = worker });
        await run.CallAsync(user, "resume", new() { ["session"] = worker });
        await run.CallAsync(user, "send", new() { ["session"] = worker, ["text"] = "Go on" });
        await AssertNoFleetToolsAsync(run, worker, launch: 1);
    }

    /// <summary>The grant is the root's config's as it is at each call: taking it away there refuses the next call, with no relaunch.</summary>
    [Fact]
    public async Task TheGrant_IsTheRootConfigs_AtEachCall()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (overseerId, fleetEntry) = await OverseerAsync(run);
        Assert.Equal(HttpStatusCode.OK, await run.InitializeAsync(GodModeMcp.FleetPath, fleetEntry.Token, overseerId));

        run.WriteActionConfig(OverseerAction, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, await run.InitializeAsync(GodModeMcp.FleetPath, fleetEntry.Token, overseerId));

        run.WriteActionConfig(OverseerAction, """{ "fleetTools": "grantable" }""");
        Assert.Equal(HttpStatusCode.Forbidden, await run.InitializeAsync(GodModeMcp.FleetPath, fleetEntry.Token, overseerId));

        run.WriteActionConfig(OverseerAction, """{ "fleetTools": true }""");
        Assert.Equal(HttpStatusCode.OK, await run.InitializeAsync(GodModeMcp.FleetPath, fleetEntry.Token, overseerId));
    }

    /// <summary>The server's own credential is the user's, who may grant: an overseer outside GodMode starts an epic overseer.</summary>
    [Fact]
    public async Task TheServersCredential_GrantsAChildOfAGrantableAction()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        await using var user = await run.ConnectFleetAsync();

        var epic = await run.StartSessionAsync(user, "epic", GrantableAction, new() { ["fleet_tools"] = true });

        Assert.Null(ParentOf(epic));
        var entry = GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(IdOf(epic), launch => launch.Stdin.Count > 0));
        Assert.Equal(HttpStatusCode.OK, await run.InitializeAsync(GodModeMcp.FleetPath, entry.Token, IdOf(epic)));
    }

    /// <summary>
    /// A deleted overseer's grant is no one's: a state folder planted under its id, in another folder of its root (as a
    /// session can make in its own), with its very status and settings, is recovered after a restart without the tools.
    /// </summary>
    [Fact]
    public async Task AStateFolderPlantedUnderADeletedOverseersId_HasNoGrant_AfterARestart()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (overseerId, _) = await OverseerAsync(run);
        var sessionId = overseerId.Split('/')[^1];
        var state = Directory.GetFiles(run.StatePath(overseerId))
            .Where(file => Path.GetFileName(file) is "status.json" or "settings.json")
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllText);

        await run.Client.Hub.InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.DeleteProject), overseerId, false);

        var planted = GodMode.ProjectFiles.SessionState.PathOf(Path.Combine(run.RootPath, "planted"), sessionId);
        Directory.CreateDirectory(planted);
        foreach (var (file, text) in state) File.WriteAllText(Path.Combine(planted, file), text);

        // The restart recovers it, purges the trash, and carries it on, as it was working
        await run.RestartAsync();
        Assert.True(await Lifecycle.LifecycleHarness.WaitForAsync(async () =>
            (await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Any(s => s.Id == overseerId)), "the planted session was not recovered");
        await using var user = await run.ConnectFleetAsync();
        if ((await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), overseerId)).State == Shared.Enums.ProjectState.Stopped)
            await run.CallAsync(user, "send", new() { ["session"] = overseerId, ["text"] = "Go on" });

        await AssertNoFleetToolsAsync(run, overseerId);
    }

    /// <summary>The fleet's endpoint with a trailing slash is the fleet's endpoint, as routing has it, for every caller.</summary>
    [Fact]
    public async Task TheFleetEndpoint_WithATrailingSlash_IsTheFleetEndpoint()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var (overseerId, fleetEntry) = await OverseerAsync(run);
        var worker = await run.CreateOverHubAsync("worker", WorkAction);
        var workerToken = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(worker, launch => launch.Stdin.Count > 0)).Token;

        Assert.Equal(HttpStatusCode.OK, await run.InitializeAsync(GodModeMcp.FleetPath + "/", ServerProcess.ApiKey));
        Assert.Equal(HttpStatusCode.OK, await run.InitializeAsync(GodModeMcp.FleetPath + "/", fleetEntry.Token, overseerId));
        Assert.Equal(HttpStatusCode.Forbidden, await run.InitializeAsync(GodModeMcp.FleetPath + "/", workerToken, worker));
    }

    [Fact]
    public async Task AGrant_ToAnActionThatStartsNoSession_IsRefused()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        await using var user = await run.ConnectFleetAsync();

        var arguments = StartArguments("fresh", ProvisionAction, new() { ["fleet_tools"] = true });
        Assert.Contains("fleetTools", await run.RefusedAsync(user, "start_session", arguments));
    }
}
