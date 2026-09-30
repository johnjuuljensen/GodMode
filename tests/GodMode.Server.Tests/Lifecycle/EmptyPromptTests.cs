using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using static GodMode.Server.Tests.Lifecycle.ProjectLifecycleTests;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A create needs a name, and a description only where the root's schema asks for one (#352). A
/// session created with no prompt starts claude with no input: it is Idle, waiting for its first
/// message, which needs nothing of the user, and claude is never sent a turn the user did not write.
/// </summary>
public class EmptyPromptTests
{
    /// <summary>claude as it is: it writes nothing until it has read its first input.</summary>
    private static FakeScript WaitsForItsFirstMessage() =>
        new FakeScript().AwaitStdin().EmitInit().EmitAssistant("The draft is in mail.md.").EmitResult();

    [Fact]
    public async Task TheDefaultSchema_RequiresOnlyTheName_AndStillOffersThePrompt()
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());

        var root = Assert.Single(await harness.Projects.ListProjectRootsAsync(), r => r.Name == LifecycleHarness.RootName);
        var schema = Assert.Single(root.Actions!).InputSchema!.Value;

        Assert.Equal(["name"], schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()));
        Assert.True(schema.GetProperty("properties").TryGetProperty("prompt", out _), "the form should still offer a description");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task Create_WithNoPrompt_IsListedIdle_AndSendsClaudeNothing(string? prompt)
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());

        var created = await harness.CreateProjectAsync(prompt: prompt);

        Assert.Equal(ProjectState.Idle, created.State);
        Assert.Equal(ProjectState.Idle, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Contains(await harness.Projects.ListProjectsAsync(), p => p.Id == created.Id && p.State == ProjectState.Idle);
        Assert.Empty(harness.Projects.GetAttention());
        var launch = await harness.WaitForLaunchAsync(created.Id, _ => true);
        Assert.NotNull(launch.ArgValue("--session-id"));
        Assert.True(LifecycleHarness.IsProcessAlive(launch.Pid), "claude should be running, waiting for its first message");
        Assert.Equal(launch.Pid, harness.ProjectInfo(created.Id).Process.ProcessId);

        // A stop closes claude's input and waits for its exit: whatever it was sent, it has read and recorded
        await harness.Projects.StopProjectAsync(created.Id);
        Assert.Empty(Assert.Single(harness.Launches(created.Id)).Stdin);
        Assert.Equal("", File.ReadAllText(Path.Combine(harness.StatePath(created.Id), "input.jsonl")));
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id), s => s.State is ProjectState.Running or ProjectState.Error);
    }

    [Fact]
    public async Task TheFirstMessage_StartsTheFirstTurn_InTheSameProcess()
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());
        var created = await harness.CreateProjectAsync(prompt: null);
        var launch = await harness.WaitForLaunchAsync(created.Id, _ => true);

        // What the app sends from the project's input box
        await harness.Projects.ReplyAndResumeAsync(created.Id, "Draft the mail to the supplier");

        var first = await harness.WaitForStdinAsync(created.Id);
        Assert.Equal(launch.Pid, first.Pid);
        Assert.Equal("Draft the mail to the supplier", PromptText(Assert.Single(first.Stdin)));
        var idle = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        Assert.NotNull(idle.LastResultAt);
        Assert.Contains(harness.Hub.StatusPushes(created.Id), s => s.State == ProjectState.Running);
        Assert.Single(harness.Launches(created.Id));
    }

    /// <summary>How a never-messaged session comes to have no process: stopped by the user, by a restart, or by claude failing.</summary>
    public enum Ended { Stopped, Restarted, Crashed }

    /// <summary>
    /// A session never sent a message has no conversation for claude to resume: its first message
    /// starts it on its own id (<c>--session-id</c>, no <c>--resume</c>), and is its only turn, with
    /// no "continue" before it. A --resume would be rejected here (<see cref="FakeScript.RejectResume"/>).
    /// </summary>
    [Theory]
    [InlineData(Ended.Stopped)]
    [InlineData(Ended.Restarted)]
    [InlineData(Ended.Crashed)]
    public async Task TheFirstMessage_ToANeverMessagedSessionWithNoProcess_IsItsOnlyTurn(Ended ended)
    {
        await using var harness = new LifecycleHarness(ended == Ended.Crashed
            ? new FakeScript().Stderr("Error: invalid API key").Exit(1)
            : new FakeScript().RejectResume().AwaitStdin().EmitInit().EmitAssistant("Hello.").EmitResult());
        var created = await harness.CreateProjectAsync(prompt: null);
        var sessionId = (await harness.WaitForLaunchAsync(created.Id, _ => true)).ArgValue("--session-id");
        switch (ended)
        {
            case Ended.Stopped: await harness.Projects.StopProjectAsync(created.Id); break;
            case Ended.Restarted: await harness.RestartAsync(); break;
            case Ended.Crashed:
                await harness.WaitForStateAsync(created.Id, ProjectState.Error);
                harness.UseScript(new FakeScript().RejectResume().AwaitStdin().EmitInit().EmitAssistant("Hello.").EmitResult());
                break;
        }
        // An idle session was doing nothing the restart carries on with
        Assert.Single(harness.Launches(created.Id));

        await harness.Projects.ReplyAndResumeAsync(created.Id, "Draft the mail to the supplier");

        var started = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Equal(sessionId, started.ArgValue("--session-id"));
        Assert.Null(started.ArgValue("--resume"));
        Assert.Equal("Draft the mail to the supplier", PromptText(Assert.Single(started.Stdin)));
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.StopProjectAsync(created.Id);
        Assert.Single(harness.Launches(created.Id)[1].Stdin);
        Assert.Equal(2, harness.Launches(created.Id).Count);
    }

    /// <summary>Resume (the header's pill) on a never-messaged session sends claude nothing: running, it has nothing to do; stopped, it starts claude waiting.</summary>
    [Fact]
    public async Task Resume_OfANeverMessagedSession_SendsNothing_RunningOrStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().RejectResume().AwaitStdin().EmitInit().EmitAssistant("Hello.").EmitResult());
        var created = await harness.CreateProjectAsync(prompt: null);
        var sessionId = (await harness.WaitForLaunchAsync(created.Id, _ => true)).ArgValue("--session-id");

        await harness.Projects.ResumeProjectAsync(created.Id);

        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
        await harness.Projects.StopProjectAsync(created.Id);
        Assert.Empty(Assert.Single(harness.Launches(created.Id)).Stdin);

        await harness.Projects.ResumeProjectAsync(created.Id);

        var resumed = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        Assert.Equal(sessionId, resumed.ArgValue("--session-id"));
        Assert.Null(resumed.ArgValue("--resume"));
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
        await harness.Projects.StopProjectAsync(created.Id);
        Assert.Empty(harness.Launches(created.Id)[1].Stdin);
        Assert.Equal(2, harness.Launches(created.Id).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARootSchemaRequiringThePrompt_RefusesACreateWithoutOne(string? prompt)
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());
        RequirePrompt(harness);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(prompt: prompt));

        Assert.Contains("'prompt'", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "p1")), "a refused create should write nothing");
    }

    [Fact]
    public async Task ARootSchemaRequiringThePrompt_CreatesWithOne_AsBefore()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        RequirePrompt(harness);

        var created = await harness.CreateProjectAsync(prompt: "Fix the flaky test");

        Assert.Equal(ProjectState.Running, created.State);
        Assert.Equal("Fix the flaky test", PromptText(Assert.Single((await harness.WaitForStdinAsync(created.Id)).Stdin)));
    }

    /// <summary>The root's own schema.json, for its one action, requiring a description.</summary>
    private static void RequirePrompt(LifecycleHarness harness) =>
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "schema.json"), """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "title": "Name" },
                "prompt": { "type": "string", "title": "Task", "x-multiline": true }
              },
              "required": ["name", "prompt"]
            }
            """);
}
