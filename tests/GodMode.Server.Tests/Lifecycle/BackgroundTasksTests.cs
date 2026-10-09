using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// What a session's claude runs in the background (issue #432, <see cref="ProjectStatus.BackgroundTasks"/>): kept from
/// claude's <c>system/background_tasks_changed</c>, the whole list each time, between turns too, with each task's step
/// from its <c>system/task_progress</c>. An empty list clears it. claude's exit, a stop and a server start clear it, as
/// claude, killed, says nothing more of its tasks; a <c>/clear</c>, which they outlive, does not.
/// </summary>
public class BackgroundTasksTests
{
    private static readonly (string, string, string) Agent = ("a1", "local_agent", "Review the diff");
    private static readonly (string, string, string) Shell = ("b1", "local_bash", "Watch CI");

    // {{session_id}} is FakeScript.SessionIdPlaceholder
    private const string Reset =
        """{"type":"conversation_reset","new_conversation_id":"0b6f3c1e-1d2a-4c7e-9f00-5a6b7c8d9e0f","trigger":"clear","session_id":"{{session_id}}"}""";

    private const string SilentResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","session_id":"{{session_id}}","usage":{"input_tokens":0,"output_tokens":0}}""";

    /// <summary>A first turn that starts a background agent, then a shell, after it has ended, the agent at a step.</summary>
    private static FakeScript Started() => new FakeScript().EmitInit()
        .AwaitStdin().EmitAssistant("Started them.").EmitBackgroundTasks(Agent).Sleep(50).EmitResult("Started them.")
        .EmitTaskProgress("a1", "Running the tests")
        // A foreground task's step, or a background task's own foreground shell's: of no task in the list
        .EmitTaskProgress("x9", "Running ls")
        .EmitBackgroundTasks(Agent, Shell);

    private static readonly BackgroundTask[] Both =
        [new("a1", "local_agent", "Review the diff", "Running the tests"), new("b1", "local_bash", "Watch CI")];

    private static Task<ProjectStatus> BothListedAsync(LifecycleHarness harness, string id) =>
        harness.WaitForStatusPushAsync(id, s => s.BackgroundTasks is { Count: 2 } tasks && tasks[0].Step != null);

    [Fact]
    public async Task TheList_IsKeptBetweenTurns_WithItsSteps_SavedAndListed_UntilClaudeListsNone()
    {
        var done = Path.Combine(Path.GetTempPath(), $"tasks-done-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(Started()
            .AwaitFile(done).EmitBackgroundTasks(Shell).EmitBackgroundTasks()
            .AwaitStdin());
        try
        {
            var created = await harness.CreateProjectAsync();

            var listed = await BothListedAsync(harness, created.Id);

            // The turn has ended: the session is idle, with its tasks
            Assert.Equal(ProjectState.Idle, listed.State);
            Assert.Equal(Both, listed.BackgroundTasks);
            Assert.Equal(Both, harness.ReadStatusFile(created.Id).BackgroundTasks);
            Assert.Equal(Both, Assert.Single(await harness.Projects.ListProjectsAsync(), p => p.Id == created.Id).BackgroundTasks);

            File.WriteAllText(done, "");
            var none = await harness.WaitForStatusPushAsync(created.Id, s => s.BackgroundTasks == null, skip: harness.Hub.StatusPushes(created.Id).Count);

            Assert.Equal(ProjectState.Idle, none.State);
            Assert.Null(harness.ReadStatusFile(created.Id).BackgroundTasks);
            Assert.Null(Assert.Single(await harness.Projects.ListProjectsAsync(), p => p.Id == created.Id).BackgroundTasks);
        }
        finally
        {
            File.Delete(done);
        }
    }

    [Fact]
    public async Task ClaudeExiting_ClearsThem_ItListsNoneOnceKilled()
    {
        var crash = Path.Combine(Path.GetTempPath(), $"tasks-crash-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(Started().AwaitFile(crash).Stderr("crashed").Exit(1));
        try
        {
            var created = await harness.CreateProjectAsync();
            await BothListedAsync(harness, created.Id);

            File.WriteAllText(crash, "");
            var exited = await harness.WaitForStateAsync(created.Id, ProjectState.Error);

            Assert.Null(exited.BackgroundTasks);
            Assert.Null(harness.ReadStatusFile(created.Id).BackgroundTasks);
        }
        finally
        {
            File.Delete(crash);
        }
    }

    [Fact]
    public async Task AStop_ClearsThem()
    {
        await using var harness = new LifecycleHarness(Started().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await BothListedAsync(harness, created.Id);

        await harness.Projects.StopProjectAsync(created.Id);

        var stopped = await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        Assert.Null(stopped.BackgroundTasks);
        Assert.Null(harness.ReadStatusFile(created.Id).BackgroundTasks);
    }

    [Fact]
    public async Task AClear_KeepsThem_TheyOutliveIt()
    {
        await using var harness = new LifecycleHarness(Started()
            .AwaitStdin().Emit(Reset).EmitInit().Emit(SilentResult)
            .AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await BothListedAsync(harness, created.Id);

        var before = harness.Hub.StatusPushes(created.Id).Count;
        await harness.Projects.SendInputAsync(created.Id, "/clear");

        // The new conversation's init makes the session Running, and the clear's silent result Idle again: read from the
        // statuses pushed, not output.jsonl, which the reset starts over
        var started = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Running, skip: before);
        var cleared = await harness.WaitForStatusPushAsync(created.Id, s => s is { LastResult: null, State: ProjectState.Idle },
            skip: harness.Hub.StatusPushes(created.Id).ToList().IndexOf(started) + 1);
        Assert.Equal(Both, started.BackgroundTasks);
        Assert.Equal(Both, cleared.BackgroundTasks);
        Assert.Equal(Both, (await harness.Projects.GetStatusAsync(created.Id)).BackgroundTasks);
    }

    [Fact]
    public async Task AServerStart_ClearsTheTasksAStatusFileKept_NoClaudeRunsThem()
    {
        await using var harness = new LifecycleHarness(new FakeScript());
        var planted = new ProjectStatus(LifecycleHarness.PlantedId(), "planted", ProjectState.Idle, DateTime.UtcNow, DateTime.UtcNow,
            null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0, BackgroundTasks: Both);
        LifecycleHarness.PlantSession(Path.Combine(harness.RootPath, "planted"), status: planted);

        await harness.Projects.RecoverProjectsAsync();

        var recovered = await harness.Projects.GetStatusAsync(LifecycleHarness.PlantedId());
        Assert.Equal(ProjectState.Idle, recovered.State);
        Assert.Null(recovered.BackgroundTasks);
        Assert.Null(harness.ReadStatusFile(LifecycleHarness.PlantedId()).BackgroundTasks);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"background_tasks_changed"}""")]
    [InlineData("""{"type":"system","subtype":"background_tasks_changed","tasks":{}}""")]
    [InlineData("""{"type":"system","subtype":"background_tasks_changed","tasks":[""")]
    public void AListThatCannotBeRead_ChangesNothing(string line) =>
        Assert.Null(StatusUpdater.BackgroundTasksOf(line, Both));

    [Fact]
    public void AList_KeepsTheStepsOfTheTasksItKeeps_AndSkipsATaskWithNoId()
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "system", subtype = "background_tasks_changed",
            tasks = new object[]
            {
                new { task_id = "b1", task_type = "local_bash", description = "Watch CI" },
                new { task_id = "a1", task_type = "local_agent", description = "Review the diff" },
                new { task_type = "local_bash", description = "no id" },
            },
        });

        Assert.Equal([Both[1], Both[0]], StatusUpdater.BackgroundTasksOf(line, Both));
        Assert.Empty(StatusUpdater.BackgroundTasksOf("""{"tasks":[]}""", Both)!);
    }

    [Fact]
    public void AStep_IsItsTasks_AndOnlyAChangedOneChangesTheList()
    {
        static string Progress(string id, string step) => JsonSerializer.Serialize(new { type = "system", subtype = "task_progress", task_id = id, description = step });

        Assert.Equal("Linting", StatusUpdater.WithStep(Both, Progress("b1", "Linting"))![1].Step);
        Assert.Null(StatusUpdater.WithStep(Both, Progress("a1", "Running the tests")));
        Assert.Null(StatusUpdater.WithStep(Both, Progress("x9", "Running ls")));
        Assert.Null(StatusUpdater.WithStep(null, Progress("a1", "Linting")));
    }
}
