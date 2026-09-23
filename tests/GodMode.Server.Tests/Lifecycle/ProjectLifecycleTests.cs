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
    /// An assistant question followed at once by its result must end in WaitingInput, in memory and in
    /// status.json. Several projects at once make an interleaving of the two lines' handlers likely.
    /// </summary>
    [Fact(Skip = "fixed by #159")]
    public async Task AssistantQuestionThenResult_IsWaitingInput_EveryTime()
    {
        const int projects = 20;
        await using var harness = new LifecycleHarness(
            new FakeScript().AwaitStdin().EmitAssistant("Which branch should I use?").EmitResult());

        var created = await Task.WhenAll(Enumerable.Range(1, projects).Select(i => harness.CreateProjectAsync($"q{i}")));

        foreach (var project in created)
        {
            var status = await harness.WaitForStateAsync(project.Id, ProjectState.WaitingInput);
            Assert.Equal("Which branch should I use?", status.CurrentQuestion);
            await LifecycleHarness.WaitUntilAsync(
                () => Task.FromResult(harness.ReadStatusFile(project.Id).State == ProjectState.WaitingInput),
                TimeSpan.FromSeconds(2),
                () => $"status.json of {project.Id} is not WaitingInput.\n{harness.Describe(project.Id)}");
        }
    }

    /// <summary>The text of a stream-json user message as GodMode writes it to claude's stdin.</summary>
    private static string PromptText(string stdinLine)
    {
        using var message = JsonDocument.Parse(stdinLine);
        Assert.Equal("user", message.RootElement.GetProperty("type").GetString());
        return message.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
