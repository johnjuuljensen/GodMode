using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// The fleet's <c>list_sessions</c> and <c>read</c> give what a session's claude runs in the background (issue #432): an
/// overseer tells a worker that is idle and still working from one that is done.
/// </summary>
public class FleetBackgroundTasksTests
{
    [Fact]
    public async Task ListSessionsAndRead_GiveTheBackgroundTasks_WithTheirSteps()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit()
            .AwaitStdin().EmitAssistant("Started it.").Sleep(50).EmitResult("Started it.")
            .EmitBackgroundTasks(("a1", "local_agent", "Review the diff")).EmitTaskProgress("a1", "Running the tests")
            .AwaitStdin());
        await using var fleet = await run.ConnectFleetAsync();
        var id = await run.StartAsync(fleet, "p1");
        await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle && s.BackgroundTasks is [{ Step: not null }], run.Server);

        var listed = Assert.Single((await run.CallAsync(fleet, "list_sessions")).EnumerateArray());
        Assert.Equal("Idle", listed.GetProperty("State").GetString());
        var task = Assert.Single(listed.GetProperty("BackgroundTasks").EnumerateArray());
        Assert.Equal(("a1", "local_agent", "Review the diff", "Running the tests"),
            (task.GetProperty("Id").GetString(), task.GetProperty("Type").GetString(), task.GetProperty("Description").GetString(),
                task.GetProperty("Step").GetString()));

        var read = await run.CallAsync(fleet, "read", new() { ["session"] = id });
        Assert.Equal("Running the tests", Assert.Single(read.GetProperty("BackgroundTasks").EnumerateArray()).GetProperty("Step").GetString());
    }
}
