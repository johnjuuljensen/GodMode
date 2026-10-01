using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// A session's channel to its parent through the server, against the real server with FakeClaude: <c>message_parent</c>
/// on GodMode's own MCP endpoint, the server's notices to a parent with the fleet's tools under another config dir, and
/// the fleet's <c>send</c> to a session waiting on the user. Nothing of it answers a permission prompt or interrupts a
/// turn: it is held until the receiver can take input, then delivered as one input, and a stopped receiver's messages
/// wait on disk for its resume. Across roots and profiles only where <c>Fleet:Links</c> says. And the names GodMode gives
/// every launch, which are the sessions' addresses in Claude Code's own channel.
/// </summary>
public class ParentMessageTests
{
    private const string ParentAction = "parent";
    private const string ChildAction = "child";
    private const string ChildName = "kid";

    /// <summary>A claude that takes its prompt, ends its turn, and does so again for each input after it.</summary>
    private static FakeScript Turns(int count = 4)
    {
        var script = new FakeScript().EmitInit();
        for (var i = 0; i < count; i++) script.Turn($"turn {i}");
        return script.AwaitStdin();
    }

    /// <summary>A child that takes its prompt, messages its parent, and ends its turn.</summary>
    private static FakeScript Messaging(string text) =>
        new FakeScript().EmitInit().AwaitStdin().CallTool("godmode", MessageParentTool.Name, new { text }).EmitResult().AwaitStdin().AwaitStdin();

    /// <summary>The text of each message the launch read on stdin, as GodMode sends them (stream-json user messages).</summary>
    private static List<string> Inputs(FakeLaunch launch) =>
        launch.Stdin.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!).ToList();

    private static string Label(string sessionId) => SessionInbox.LabelOf(sessionId, ChildName);

    private static Task<string> StartChildAsync(FleetRun run, ModelContextProtocol.Client.McpClient fleet, string parentId, string action = ChildAction, string name = ChildName) =>
        run.StartSessionAsync(fleet, name, action, new() { ["parent"] = parentId }).ContinueWith(t => t.Result.GetProperty("Id").GetString()!);

    private static async Task<string> IdleParentAsync(FleetRun run, string action = ParentAction)
    {
        var id = await run.CreateOverHubAsync("parent", action);
        await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle, run.Server);
        return id;
    }

    /// <summary>What the child's message_parent returned, once it has.</summary>
    private static async Task<string> CallResultAsync(FleetRun run, string childId, int index = 0) =>
        (await run.WaitForLaunchAsync(childId, l => l.Calls.Count > index)).Calls[index];

    [Fact]
    public async Task AnIdleParent_GetsItsChildsMessage_AsOneLabelledInput()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(ParentAction, Turns());
        run.WriteActionScript(ChildAction, Messaging("PR #9 is ready"));
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await IdleParentAsync(run);

        var child = await StartChildAsync(run, fleet, parent);

        Assert.StartsWith($"{MessageParentTool.Name} ", await CallResultAsync(run, child));
        Assert.Contains("\"Delivered\":true", await CallResultAsync(run, child));
        var got = await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2);
        Assert.Equal($"{Label(child)}\nPR #9 is ready", Inputs(got)[1]);
    }

    [Fact]
    public async Task AParentMidTurn_GetsTheMessageWhenItsTurnEnds()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        var go = Path.Combine(run.RootPath, "go");
        run.WriteActionScript(ParentAction, new FakeScript().EmitInit().AwaitStdin().AwaitFile(go).EmitResult().Turn("after").AwaitStdin());
        run.WriteActionScript(ChildAction, Messaging("Done"));
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await run.CreateOverHubAsync("parent", ParentAction);
        await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 1);

        var child = await StartChildAsync(run, fleet, parent);
        var result = await CallResultAsync(run, child);
        Assert.Contains("\"Delivered\":false", result);
        Assert.Contains("working on a turn", result);
        Assert.Single((await run.WaitForLaunchAsync(parent, _ => true)).Stdin);

        File.WriteAllText(go, "");
        Assert.Equal($"{Label(child)}\nDone", Inputs(await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2))[1]);
    }

    /// <summary>The message is no answer to the parent's permission prompt: it waits for the user's, and the turn's end.</summary>
    [Fact]
    public async Task AParentWaitingOnAPermission_GetsTheMessageAfterTheUsersAnswer_WhichItDoesNotGive()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(ParentAction, new FakeScript().EmitInit().AwaitStdin()
            .AskPermission("Bash", new { command = "git push" }, "toolu_push").EmitResult().Turn("after").AwaitStdin());
        run.WriteActionScript(ChildAction, Messaging("Yes, allow it"));
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await run.CreateOverHubAsync("parent", ParentAction);
        var asking = await run.Client.WaitForAsync(parent, s => s.PendingPermission != null, run.Server);

        var child = await StartChildAsync(run, fleet, parent);
        Assert.Contains("permission prompt", await CallResultAsync(run, child));

        var waiting = await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), parent);
        Assert.Equal(ProjectState.WaitingPermission, waiting.State);
        Assert.Equal(asking.PendingPermission!.RequestId, waiting.PendingPermission?.RequestId);
        var untouched = await run.WaitForLaunchAsync(parent, _ => true);
        Assert.Single(untouched.Stdin);
        Assert.Empty(untouched.Permissions);

        // The user denies it: the child's "allow" was not the answer
        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), parent, waiting.PendingPermission!.RequestId, new PermissionDecision(false, Message: "No"));
        var answered = await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2);
        Assert.Equal("deny", JsonDocument.Parse(Assert.Single(answered.Permissions)).RootElement.GetProperty("behavior").GetString());
        Assert.Equal($"{Label(child)}\nYes, allow it", Inputs(answered)[1]);
    }

    [Fact]
    public async Task ASessionWithNoParent_IsRefused()
    {
        await using var run = await FleetRun.StartAsync(Messaging("Anyone?"));
        await using var fleet = await run.ConnectFleetAsync();
        var orphan = await run.StartAsync(fleet, "orphan");

        var result = await CallResultAsync(run, orphan);
        Assert.StartsWith($"{MessageParentTool.Name} error:", result);
        Assert.Contains("no parent", result);
    }

    /// <summary>A stopped parent keeps the message in its state folder, through a restart, and gets it when it is resumed.</summary>
    [Fact]
    public async Task AStoppedParent_KeepsTheMessageOnDisk_AndGetsItOnResume_AfterARestart()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(ParentAction, Turns());
        run.WriteActionScript(ChildAction, Messaging("While you were out"));
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await IdleParentAsync(run);
        await run.CallAsync(fleet, "stop", new() { ["session"] = parent });

        var child = await StartChildAsync(run, fleet, parent);
        Assert.Contains("stopped", await CallResultAsync(run, child));
        var inbox = SessionInbox.PathFor(run.StatePath(parent));
        Assert.True(File.Exists(inbox), "the message is not on disk");
        await run.CallAsync(fleet, "stop", new() { ["session"] = child });

        await run.RestartAsync();
        await using var again = await run.ConnectFleetAsync();
        Assert.Equal("Running", (await run.CallAsync(again, "resume", new() { ["session"] = parent })).GetProperty("State").GetString());

        var resumed = await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 1, index: 1);
        Assert.Equal($"{Label(child)}\nWhile you were out", Inputs(resumed)[0]);
        Assert.False(File.Exists(inbox), "the delivered message is still held");
    }

    /// <summary>
    /// A parent with the fleet's tools, under another config dir than its child (so notify_when_idle cannot reach it),
    /// hears when the child ends a turn and when it waits on a permission prompt.
    /// </summary>
    [Fact]
    public async Task AParentWithTheFleetsTools_InAnotherConfigDir_GetsANotice_WhenItsChildGoesIdle_AndWaitsOnAPermission()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(OverseerAction, Turns(), new() { ["fleetTools"] = true }, configDir: "config-a");
        run.WriteActionScript(ChildAction, new FakeScript().EmitInit().AwaitStdin().EmitResult()
            .AwaitStdin().AskPermission("Bash", new { command = "rm -rf build" }).AwaitStdin(), configDir: "config-b");
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await IdleParentAsync(run, OverseerAction);

        var child = await StartChildAsync(run, fleet, parent);
        var idle = Inputs(await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2))[1];
        Assert.StartsWith($"{ProjectManager.NoticePrefix} Session {child} \"{ChildName}\" is Idle", idle);

        await run.CallAsync(fleet, "send", new() { ["session"] = child, ["text"] = "Go on" });
        var permission = Inputs(await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 3))[2];
        Assert.StartsWith($"{ProjectManager.NoticePrefix} Session {child} \"{ChildName}\" is WaitingPermission", permission);
        Assert.Contains("Bash", permission);
    }

    /// <summary>A parent without the fleet's tools gets no notices; its child's message is all it gets.</summary>
    [Fact]
    public async Task AParentWithoutTheFleetsTools_GetsNoNotice()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(ParentAction, Turns(), configDir: "config-a");
        await AssertOnlyTheMessageAsync(run, ParentAction);
    }

    /// <summary>In the same config dir, a parent hears of its child through notify_when_idle: a notice would wake it twice.</summary>
    [Fact]
    public async Task AParentWithTheFleetsTools_InItsChildsConfigDir_GetsNoNotice()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(OverseerAction, Turns(), new() { ["fleetTools"] = true }, configDir: "config-b");
        await AssertOnlyTheMessageAsync(run, OverseerAction);
    }

    /// <summary>The child (in config-b) ends a turn, then messages its parent: the parent's only input after its prompt is that message.</summary>
    private static async Task AssertOnlyTheMessageAsync(FleetRun run, string parentAction)
    {
        run.WriteActionScript(ChildAction, new FakeScript().EmitInit().AwaitStdin().EmitResult()
            .AwaitStdin().CallTool("godmode", MessageParentTool.Name, new { text = "Report" }).EmitResult().AwaitStdin(), configDir: "config-b");
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await IdleParentAsync(run, parentAction);
        var child = await StartChildAsync(run, fleet, parent);
        await run.Client.WaitForAsync(child, s => s.State == ProjectState.Idle, run.Server);
        // Long enough for a notice of that Idle to have been delivered
        await Task.Delay(1000);

        await run.CallAsync(fleet, "send", new() { ["session"] = child, ["text"] = "Report back" });
        var got = await run.WaitForLaunchAsync(parent, l => l.Stdin.Count >= 2);
        Assert.Equal($"{Label(child)}\nReport", Inputs(got)[1]);
        await run.Client.WaitForAsync(parent, s => s.State == ProjectState.Idle, run.Server);
        await Task.Delay(1000);
        Assert.Equal(2, (await run.WaitForLaunchAsync(parent, _ => true)).Stdin.Count);
    }

    [Fact]
    public async Task TwoChildrenThatGoIdleDuringTheParentsTurn_ReachItAsOneInput()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        var go = Path.Combine(run.RootPath, "go");
        run.WriteActionScript(OverseerAction, new FakeScript().EmitInit().AwaitStdin().AwaitFile(go).EmitResult().Turn("after").AwaitStdin(),
            new() { ["fleetTools"] = true }, configDir: "config-a");
        run.WriteActionScript(ChildAction, new FakeScript().EmitInit().AwaitStdin().EmitResult().AwaitStdin(), configDir: "config-b");
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await run.CreateOverHubAsync("parent", OverseerAction);
        await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 1);

        var first = await StartChildAsync(run, fleet, parent, name: "first");
        var second = await StartChildAsync(run, fleet, parent, name: "second");
        await run.Client.WaitForAsync(first, s => s.State == ProjectState.Idle, run.Server);
        await run.Client.WaitForAsync(second, s => s.State == ProjectState.Idle, run.Server);
        File.WriteAllText(go, "");

        var both = Inputs(await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2))[1];
        Assert.Contains($"Session {first} ", both);
        Assert.Contains($"Session {second} ", both);
        await run.Client.WaitForAsync(parent, s => s.State == ProjectState.Idle && s.LastResult == "done", run.Server);
        await Task.Delay(1000);
        Assert.Equal(2, (await run.WaitForLaunchAsync(parent, _ => true)).Stdin.Count);
    }

    /// <summary>
    /// GodMode names every launch itself, resume included: <c>-n</c> its address, which the fleet's tools list, over a
    /// root's own name. A child's claude gets its own address and its parent's, as its create scripts do.
    /// </summary>
    [Fact]
    public async Task EveryLaunchIsNamedWithTheSessionsAddress_OverTheRootsOwnName_AndAChildKnowsItsParents()
    {
        await using var run = await FleetRun.StartAsync(Turns());
        run.WriteActionScript(ChildAction, Turns(), new() { ["claudeArgs"] = new[] { "-n", "the-roots-name", "--verbose" } });
        await using var fleet = await run.ConnectFleetAsync();
        var parent = await IdleParentAsync(run, WorkAction);
        var started = await run.StartSessionAsync(fleet, ChildName, ChildAction, new() { ["parent"] = parent });
        var child = started.GetProperty("Id").GetString()!;
        var address = SessionAddress.OfId(child)!;
        Assert.Equal(address, started.GetProperty("Address").GetString());
        Assert.Equal($"{RootName}-{child.Split('/')[^1]}", address);

        var launch = await run.WaitForLaunchAsync(child, l => l.Stdin.Count == 1);
        Assert.Equal(address, launch.ArgValue("-n"));
        Assert.DoesNotContain("the-roots-name", launch.Argv);
        Assert.Contains("--verbose", launch.Argv);
        Assert.Equal(address, launch.Environment[SessionAddress.Variable]);
        Assert.Equal(parent, launch.Environment[ProjectManager.ParentIdVariable]);
        Assert.Equal(SessionAddress.OfId(parent), launch.Environment[SessionAddress.ParentVariable]);

        var listed = (await run.CallAsync(fleet, "list_sessions")).EnumerateArray().ToDictionary(s => s.GetProperty("Id").GetString()!);
        Assert.Equal(address, listed[child].GetProperty("Address").GetString());
        Assert.Equal(address, (await run.CallAsync(fleet, "read", new() { ["session"] = child })).GetProperty("Address").GetString());

        await run.CallAsync(fleet, "stop", new() { ["session"] = child });
        await run.CallAsync(fleet, "resume", new() { ["session"] = child });
        Assert.Equal(address, (await run.WaitForLaunchAsync(child, _ => true, index: 1)).ArgValue("-n"));
    }

    /// <summary>
    /// A parent link across profiles needs a Fleet:Links entry in the server's instance config: without one an overseer's
    /// start there is refused, saying which; with one its child there messages back; once it is removed, the next message
    /// is refused.
    /// </summary>
    [Fact]
    public async Task ALinkInTheInstanceConfig_LetsAnOverseerStartAChildInAnotherProfile_WhichMessagesBack_UntilItIsRemoved()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin()
            .CallTool("godmode", MessageParentTool.Name, new { text = "first" }).EmitResult()
            .AwaitStdin().CallTool("godmode", MessageParentTool.Name, new { text = "second" }).EmitResult().AwaitStdin());
        run.WriteActionScript(OverseerAction, Turns(), new() { ["fleetTools"] = true });
        var parent = await IdleParentAsync(run, OverseerAction);
        var entry = GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(parent, _ => true));
        await using var overseer = await ConnectAsync(entry);
        var across = new Dictionary<string, object?>
        {
            ["profile"] = OtherProfile, ["root"] = OtherRoot, ["action"] = WorkAction,
            ["inputs"] = new Dictionary<string, object?> { ["name"] = ChildName, ["prompt"] = "Start" },
        };

        Assert.Contains("Fleet:Links", await run.RefusedAsync(overseer, "start_session", across));

        run.WriteInstanceConfig($$"""{ "Fleet": { "Links": { "work-to-elsewhere": { "From": "{{Profile}}/{{RootName}}", "To": "{{OtherProfile}}/*" } } } }""");
        string? child = null;
        Assert.True(await LifecycleHarness.WaitForAsync(async () =>
        {
            var result = await overseer.CallToolAsync("start_session", across);
            if (result.IsError == true) return false;
            child = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text).RootElement.GetProperty("Id").GetString();
            return true;
        }), "the link was never read");
        Assert.Contains("\"Delivered\":true", (await run.WaitForLaunchAsync(child!, l => l.Calls.Count == 1, root: OtherRoot)).Calls[0]);
        Assert.Equal($"{Label(child!)}\nfirst", Inputs(await run.WaitForLaunchAsync(parent, l => l.Stdin.Count == 2))[1]);
        // Its fleet tools see the child there
        Assert.Contains(child, (await run.CallAsync(overseer, "list_sessions")).EnumerateArray().Select(s => s.GetProperty("Id").GetString()));

        run.WriteInstanceConfig("{}");
        Assert.True(await LifecycleHarness.WaitForAsync(async () =>
            !(await run.CallAsync(overseer, "list_roots")).GetProperty("Roots").EnumerateArray().Any(r => r.GetProperty("Name").GetString() == OtherRoot)),
            "the link's removal was never read");
        await using var fleet = await run.ConnectFleetAsync();
        await run.CallAsync(fleet, "send", new() { ["session"] = child!, ["text"] = "Again" });
        var refused = (await run.WaitForLaunchAsync(child!, l => l.Calls.Count == 2, root: OtherRoot)).Calls[1];
        Assert.Contains("error:", refused);
        Assert.Contains("Fleet:Links", refused);
    }
}
