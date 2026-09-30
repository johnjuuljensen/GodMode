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

    /// <summary>
    /// A session that was never sent a message has no conversation for claude to resume: the fresh
    /// session that takes its place is sent no "continue" turn, and waits for the first message still.
    /// </summary>
    [Fact]
    public async Task Resuming_ASessionNeverSentAMessage_StartsFresh_WithNoTurn()
    {
        await using var harness = new LifecycleHarness(new FakeScript().RejectResume().AwaitStdin().EmitInit().EmitAssistant("Hello.").EmitResult());
        var created = await harness.CreateProjectAsync(prompt: null);
        var sessionId = (await harness.WaitForLaunchAsync(created.Id, _ => true)).ArgValue("--session-id");
        await harness.Projects.StopProjectAsync(created.Id);

        await harness.Projects.ResumeProjectAsync(created.Id);

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 1 && l.ArgValue("--resume") == sessionId, index: 1);
        var fresh = await harness.WaitForLaunchAsync(created.Id, l => l.ArgValue("--session-id") == sessionId, index: 2);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ProjectInfo(created.Id).Process.ProcessId == fresh.Pid), null,
            () => $"the fresh session is not the project's process.\n{harness.Describe(created.Id)}");
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);

        await harness.Projects.ReplyAndResumeAsync(created.Id, "Now start");

        var answered = await harness.WaitForStdinAsync(created.Id, index: 2);
        Assert.Equal("Now start", PromptText(Assert.Single(answered.Stdin)));
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        Assert.Equal(3, harness.Launches(created.Id).Count);
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
