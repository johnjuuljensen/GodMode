using GodMode.FakeClaude;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session that was working when the server stopped carries on by itself after the restart.
/// </summary>
public class RestartResumeTests
{
    private const string DefaultResumePrompt = "The GodMode server restarted and interrupted you. Continue where you left off.";

    /// <summary>Waits for a prompt, reports it is working, and waits for the next one.</summary>
    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    /// <summary>Waits until the project's claude has said it is working.</summary>
    private static Task WorkingAsync(LifecycleHarness harness, string projectId) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(projectId).Contains("Working on it")), null,
            () => $"claude did not start working.\n{harness.Describe(projectId)}");

    [Fact]
    public async Task Running_IsResumedAfterARestart_WithTheContinuePrompt()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await harness.CreateProjectAsync();
        var create = await harness.WaitForStdinAsync(created.Id);
        await WorkingAsync(harness, created.Id);

        await harness.RestartAsync();

        var resume = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Equal(create.ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.Equal(DefaultResumePrompt, ProjectLifecycleTests.PromptText(Assert.Single(resume.Stdin)));
        var status = await harness.WaitForStateAsync(created.Id, ProjectState.Running);
        Assert.Null(status.StateAtShutdown);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
    }
}
