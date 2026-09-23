using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// create → Running → Idle → Stop, through the real ProjectManager and ClaudeProcessManager against
/// a scripted fake claude. One test per behaviour; later issues in epic #157 add theirs here.
/// </summary>
public class ProjectLifecycleTests
{
    [Fact]
    public async Task Create_LaunchesClaudeWithThePrompt_AndIsRunning()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());

        var created = await harness.CreateProjectAsync(prompt: "Fix the flaky test");

        Assert.Equal(ProjectState.Running, created.State);
        var launch = await harness.WaitForStdinAsync(created.Id);
        Assert.True(LifecycleHarness.IsProcessAlive(launch.Pid), "the fake should still be waiting for its next turn");
        Assert.Equal(["--print", "--output-format=stream-json", "--input-format=stream-json"],
            launch.Argv.Intersect(["--print", "--output-format=stream-json", "--input-format=stream-json"]));
        Assert.NotNull(launch.ArgValue("--session-id"));
        Assert.Equal("Fix the flaky test", PromptText(Assert.Single(launch.Stdin)));
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Fact]
    public async Task AssistantThenResult_IsIdle()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done."));

        var created = await harness.CreateProjectAsync();

        var status = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        Assert.Null(status.CurrentQuestion);
        Assert.Equal(ProjectState.Idle, harness.ReadStatusFile(created.Id).State);
    }

    [Fact]
    public async Task SendInput_ReachesClaude_AndStartsANewTurn()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().Turn("First answer.").AwaitStdin().Sleep(200).EmitAssistant("Second answer.").EmitResult());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        await harness.Projects.SendInputAsync(created.Id, "And now the docs");

        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(created.Id)).State);
        var launch = await harness.WaitForStdinAsync(created.Id, count: 2);
        Assert.Equal("And now the docs", PromptText(launch.Stdin[1]));
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
    }

    [Fact]
    public async Task Stop_KillsTheProcess_AndIsStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        await harness.Projects.StopProjectAsync(created.Id);

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Equal(ProjectState.Stopped, harness.ReadStatusFile(created.Id).State);
        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after Stop");
    }

    [Fact(Skip = "fixed by #160")]
    public async Task ProcessExitsWithAnError_IsError()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().Stderr("fatal: something broke").Exit(1));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 1);
        await harness.WaitForStateAsync(created.Id, ProjectState.Error);
    }

    [Fact(Skip = "fixed by #160")]
    public async Task ProcessExitsCleanlyAfterAResult_IsStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done.").Exit(0));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 0);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
    }

    /// <summary>
    /// An assistant question followed at once by its result (one burst on stdout) must end in
    /// WaitingInput, in memory and in status.json. Each project is one trial, stopped before the next
    /// starts so live processes do not pile up.
    /// </summary>
    [Fact]
    public async Task AssistantQuestionThenResult_IsWaitingInput_EveryTime()
    {
        const int trials = 20;
        const string question = "Which branch should I use?";
        await using var harness = new LifecycleHarness(new FakeScript().AwaitStdin().EmitAssistant(question).EmitResult());
        var stale = new List<string>();

        for (var i = 1; i <= trials; i++)
        {
            var project = await harness.CreateProjectAsync($"q{i}");
            var status = await harness.WaitForStateAsync(project.Id, ProjectState.WaitingInput);
            Assert.Equal(question, status.CurrentQuestion);
            if (!await LifecycleHarness.WaitForAsync(
                    () => Task.FromResult(harness.ReadStatusFile(project.Id).State == ProjectState.WaitingInput),
                    TimeSpan.FromMilliseconds(500)))
                stale.Add($"{project.Id}\n{harness.Describe(project.Id)}");
            await harness.Projects.StopProjectAsync(project.Id);
        }

        Assert.True(stale.Count == 0,
            $"{stale.Count} of {trials} projects are WaitingInput in memory but not in status.json. First: {stale.FirstOrDefault()}");
    }

    /// <summary>Two user sends at once reach claude as two whole stream-json lines.</summary>
    [Fact]
    public Task ConcurrentSendInput_WritesTwoWholeLines() =>
        TwoSendsAtOnce_WriteTwoWholeLines((harness, projectId, input) => harness.Projects.SendInputAsync(projectId, input));

    /// <summary>
    /// The same below the project's state lock, which user sends also take: sends that meet at the
    /// process (the initial prompt and a send, say) are kept apart by the stdin lock alone.
    /// </summary>
    [Fact]
    public Task ConcurrentProcessSends_WriteTwoWholeLines() =>
        TwoSendsAtOnce_WriteTwoWholeLines((harness, projectId, input) =>
            harness.ProcessManager.SendInputAsync(harness.ProjectInfo(projectId), input));

    /// <summary>
    /// Two sends at once, not one interleaved or lost line. The messages are large so that each
    /// write takes several pipe writes, which is where they would mix.
    /// </summary>
    private static async Task TwoSendsAtOnce_WriteTwoWholeLines(Func<LifecycleHarness, string, string, Task> send)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("Ready."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var first = new string('a', 256 * 1024);
        var second = new string('b', 256 * 1024);

        await Task.WhenAll(
            Task.Run(() => send(harness, created.Id, first)),
            Task.Run(() => send(harness, created.Id, second)));

        var launch = await harness.WaitForStdinAsync(created.Id, count: 3);
        Assert.Equal(3, launch.Stdin.Count);
        Assert.Equal([first, second], launch.Stdin.Skip(1).Select(PromptText).Order());
    }

    /// <summary>The text of a stream-json user message as GodMode writes it to claude's stdin.</summary>
    private static string PromptText(string stdinLine)
    {
        using var message = JsonDocument.Parse(stdinLine);
        Assert.Equal("user", message.RootElement.GetProperty("type").GetString());
        return message.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
