using GodMode.FakeClaude;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// <see cref="IProjectHub.GetLastReplies"/> over the real hub, on a real session (#378): what claude said in its last
/// turns, oldest first, read from <c>output.jsonl</c> whether or not the session needs the user, as the fleet's
/// <c>read</c> gives them. The voice reads a project's last reply with it.
/// </summary>
public class LastRepliesHubTests
{
    [Fact]
    public async Task GetLastReplies_GivesTheLastTurns_OldestFirst_SeenOrNot()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().Turn("The first answer").Turn("The second answer").AwaitStdin());
        var hub = run.Client.Hub;
        var id = await run.CreateOverHubAsync("p1", FleetRun.WorkAction);
        await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle && s.LastResult != null, run.Server);

        var first = await hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), id, 1);
        Assert.Equal([new AssistantReply("The first answer", true)], first);

        // Seen, it needs the user no more: its reply is read all the same
        await hub.InvokeAsync(nameof(IProjectHub.MarkSeen), id);
        await hub.InvokeAsync(nameof(IProjectHub.SendInput), id, "Go on");
        Assert.True(await LifecycleHarness.WaitForAsync(async () =>
            (await hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), id, 2)).Length == 2
            && (await hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id)).State == ProjectState.Idle), run.Server.Output);
        await hub.InvokeAsync(nameof(IProjectHub.MarkSeen), id);
        Assert.Empty(await hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention)));

        Assert.Equal([new AssistantReply("The second answer", true)],
            await hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), id, 1));
        Assert.Equal([new AssistantReply("The first answer", true), new AssistantReply("The second answer", true)],
            await hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), id, IProjectHub.MaxReplyTurns));
    }

    [Fact]
    public async Task GetLastReplies_RefusesTurnsOutOfRange_AndAProjectItDoesNotTrack()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().Turn("An answer").AwaitStdin());
        var hub = run.Client.Hub;
        var id = await run.CreateOverHubAsync("p1", FleetRun.WorkAction);

        foreach (var turns in new[] { 0, IProjectHub.MaxReplyTurns + 1 })
            Assert.Contains("turns", (await Assert.ThrowsAsync<HubException>(() =>
                hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), id, turns))).Message);
        Assert.Contains("nope", (await Assert.ThrowsAsync<HubException>(() =>
            hub.InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), "nope", 1))).Message);
    }
}
