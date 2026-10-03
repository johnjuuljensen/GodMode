using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// The fleet and the user's inbox (issue #401), against the real server with FakeClaude: an overseer's <c>escalate</c>
/// is an item in the hub's list, and its child's finished turn is in the overseer's <c>list_sessions</c>, not the user's list.
/// </summary>
public class FleetInboxTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin().AwaitStdin();

    private static Task<AttentionItem[]> AttentionAsync(FleetRun run) =>
        run.Client.Hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention));

    [Fact]
    public async Task AnOverseersEscalation_IsAnItemInTheUsersList_UntilSeen_AndTheServersCredentialCannotEscalate()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        var overseer = await run.CreateOverHubAsync("overseer", OverseerAction);
        await using var fleet = await ConnectAsync(GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(overseer, launch => launch.Stdin.Count > 0)));

        await run.CallAsync(fleet, "escalate", new() { ["text"] = "#376 needs your decision.", ["url"] = "https://github.com/o/r/issues/376" });

        var item = Assert.Single(await AttentionAsync(run));
        Assert.Equal((overseer, AttentionKind.Escalation, "#376 needs your decision.", "https://github.com/o/r/issues/376"),
            (item.ProjectId, item.Kind, item.Text, item.PullRequestUrl));
        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.MarkSeen), overseer);
        Assert.Empty(await AttentionAsync(run));

        await using var server = await run.ConnectFleetAsync();
        Assert.Contains("Only a GodMode session", await run.RefusedAsync(server, "escalate", new() { ["text"] = "Decide." }));
    }

    [Fact]
    public async Task AChildsFinishedTurn_IsInItsOverseersListOfSessions_AndNotInTheUsersList()
    {
        await using var run = await FleetRun.StartAsync(Waiting());
        run.WriteActionScript(WorkAction, new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Done.").Sleep(50).EmitResult("Done.").AwaitStdin());
        var overseer = await run.CreateOverHubAsync("overseer", OverseerAction);
        await using var fleet = await ConnectAsync(GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(overseer, launch => launch.Stdin.Count > 0)));
        var child = await run.StartAsync(fleet, "worker");

        JsonElement entry = default;
        Assert.True(await Lifecycle.LifecycleHarness.WaitForAsync(async () =>
            (entry = (await run.CallAsync(fleet, "list_sessions")).EnumerateArray().Single(s => s.GetProperty("Id").GetString() == child))
                .TryGetProperty("Needs", out var needs) && needs.GetString() == nameof(AttentionKind.Finished)), $"the child's turn did not finish: {entry}\n{run.Server.Output}");

        Assert.DoesNotContain(await AttentionAsync(run), item => item.ProjectId == child);
        var summaries = await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects));
        Assert.Equal(overseer, summaries.Single(s => s.Id == child).RecordedParentId);
    }
}
