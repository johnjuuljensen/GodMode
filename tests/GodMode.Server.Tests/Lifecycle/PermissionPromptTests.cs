using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Permission prompts in the project's lifecycle. The bridge's call is made the way the internal
/// endpoint makes it, <see cref="IProjectManager.RequestPermissionAsync"/> with the request's abort
/// token; <see cref="PermissionPromptEndToEndTests"/> runs the same through HTTP and SignalR.
/// </summary>
public class PermissionPromptTests
{
    private static PermissionPromptRequest Bash(string command) =>
        new("Bash", JsonSerializer.SerializeToElement(new { command }), "toolu_1");

    private static async Task<(LifecycleHarness Harness, ProjectStatus Created)> RunningAsync()
    {
        var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        return (harness, created);
    }

    [Fact]
    public async Task Launch_AsksThroughTheBridge_WithoutTheAskUserQuestionPrompt()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;

        var launch = harness.Launches(created.Id)[0];
        Assert.Equal("mcp__godmode-bridge__permission_prompt", launch.ArgValue("--permission-prompt-tool"));
        Assert.Equal("host", launch.ArgValue("--permission-prompts"));
        Assert.DoesNotContain("--append-system-prompt", launch.Argv);
        Assert.DoesNotContain("--dangerously-skip-permissions", launch.Argv);
    }

    [Fact]
    public async Task Request_IsWaitingPermission_WithItsSummary_UntilAllowed()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;

        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("git push origin feature/12-x"), CancellationToken.None);

        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.WaitingPermission);
        Assert.Equal("Bash: git push origin feature/12-x", waiting.PendingPermission!.Summary);
        Assert.Equal("Bash", waiting.PendingPermission.ToolName);
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal(ProjectState.WaitingPermission, onDisk.State);
        Assert.Equal(waiting.PendingPermission.RequestId, onDisk.PendingPermission?.RequestId);
        Assert.False(asking.IsCompleted);

        await harness.Projects.RespondToPermissionAsync(created.Id, waiting.PendingPermission.RequestId, new PermissionDecision(true));

        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("allow", result.Behavior);
        Assert.Equal("git push origin feature/12-x", result.UpdatedInput!.Value.GetProperty("command").GetString());
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Running, status.State);
        Assert.Null(status.PendingPermission);
        Assert.Null(harness.ReadStatusFile(created.Id).PendingPermission);
    }

    [Fact]
    public async Task Request_SurvivesTheClientThatSawItDisconnecting()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var client = harness.Connect("c1");
        await client.SubscribeAsync(created.Id, 0);
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("rm -rf build"), CancellationToken.None);
        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        await client.DisconnectAsync();

        Assert.False(asking.IsCompleted);
        var listed = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(waiting.PendingPermission!.RequestId, listed.PendingPermission?.RequestId);
        await harness.Projects.RespondToPermissionAsync(created.Id, waiting.PendingPermission.RequestId,
            new PermissionDecision(false, "Not now"));
        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("deny", result.Behavior);
        Assert.Equal("Not now", result.Message);
    }

    [Fact]
    public async Task TwoRequests_TheOldestIsShown_ThenTheNext()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var first = harness.Projects.RequestPermissionAsync(created.Id, Bash("first"), CancellationToken.None);
        var shownFirst = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission?.Summary == "Bash: first");
        var second = harness.Projects.RequestPermissionAsync(created.Id, Bash("second"), CancellationToken.None);
        // A request is pending as soon as the call returns its task
        Assert.Equal("Bash: first", (await harness.Projects.GetStatusAsync(created.Id)).PendingPermission?.Summary);

        await harness.Projects.RespondToPermissionAsync(created.Id, shownFirst.PendingPermission!.RequestId, new PermissionDecision(true));

        Assert.Equal("allow", (await first.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.WaitingPermission, status.State);
        Assert.Equal("Bash: second", status.PendingPermission?.Summary);
        await harness.Projects.RespondToPermissionAsync(created.Id, status.PendingPermission!.RequestId, new PermissionDecision(false));
        Assert.Equal("deny", (await second.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Fact]
    public async Task AnsweredTwice_TheSecondAnswerFails()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("ls"), CancellationToken.None);
        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);
        await harness.Projects.RespondToPermissionAsync(created.Id, waiting.PendingPermission!.RequestId, new PermissionDecision(true));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RespondToPermissionAsync(
            created.Id, waiting.PendingPermission.RequestId, new PermissionDecision(false)));
        Assert.Equal("allow", (await asking).Behavior);
    }

    /// <summary>claude went away (its bridge's call dropped): the prompt is withdrawn, and the project runs on.</summary>
    [Fact]
    public async Task BridgeCallDropped_WithdrawsTheRequest()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        using var dropped = new CancellationTokenSource();
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("ls"), dropped.Token);
        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        dropped.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Running, status.State);
        Assert.Null(status.PendingPermission);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RespondToPermissionAsync(
            created.Id, waiting.PendingPermission!.RequestId, new PermissionDecision(true)));
    }

    [Fact]
    public async Task Stop_WhileWaiting_IsStopped_WithoutTheRequest_AndDeniesIt()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("ls"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        await harness.Projects.StopProjectAsync(created.Id);

        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal(ProjectState.Stopped, onDisk.State);
        Assert.Null(onDisk.PendingPermission);
        Assert.Null((await harness.Projects.GetStatusAsync(created.Id)).PendingPermission);
    }

    /// <summary>
    /// A server restart ends every request (the bridge's call fails, and claude sees a deny): the
    /// shutdown stops a project waiting on one like any running project, and persists it without it.
    /// </summary>
    [Fact]
    public async Task StoppingTheHost_WhileWaiting_PersistsStopped_WithoutTheRequest()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var launch = harness.Launches(created.Id)[0];
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("ls"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        harness.StopHost();

        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after the host stopped");
        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal(ProjectState.Stopped, onDisk.State);
        Assert.Null(onDisk.PendingPermission);
    }

    [Fact]
    public async Task ProcessExitingWhileWaiting_IsError_WithoutTheRequest()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().AwaitStdin().Stderr("boom").Exit(1));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("ls"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(created.Id), "exit now");

        var failed = await harness.WaitForStateAsync(created.Id, ProjectState.Error);
        Assert.Null(failed.PendingPermission);
        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
    }

    /// <summary>claude reads no input while it waits: a reply in the chat answers the prompt instead.</summary>
    [Fact]
    public async Task SendInput_WhileWaiting_DeniesWithTheReply_AndDoesNotReachStdin()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash("git push --force"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        await harness.Projects.SendInputAsync(created.Id, "no, push without --force");

        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("deny", result.Behavior);
        Assert.Contains("no, push without --force", result.Message);
        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Fact]
    public async Task AskUserQuestion_IsAPendingQuestion_AnsweredWithTheLabel()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new object[]
            {
                new { question = "Which color?", header = "Color", options = new[] { new { label = "Red" }, new { label = "Blue" } }, multiSelect = false },
                new { question = "Which size?", options = new[] { new { label = "S" }, new { label = "L" } }, multiSelect = true },
            },
        });
        var asking = harness.Projects.RequestPermissionAsync(created.Id, new PermissionPromptRequest("AskUserQuestion", input, "toolu_q"), CancellationToken.None);

        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingQuestion != null);
        Assert.Equal(ProjectState.WaitingInput, waiting.State);
        Assert.Null(waiting.PendingPermission);
        Assert.Equal("Which color?", waiting.CurrentQuestion);
        Assert.Equal(["Which color?", "Which size?"], waiting.PendingQuestion!.Questions.Select(q => q.Question));
        Assert.True(waiting.PendingQuestion.Questions[1].MultiSelect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.RespondToPermissionAsync(
            created.Id, waiting.PendingQuestion.RequestId, new PermissionDecision(true)));

        await harness.Projects.AnswerQuestionAsync(created.Id, waiting.PendingQuestion.RequestId,
            new Dictionary<string, string> { ["Which color?"] = "Blue", ["Which size?"] = "S, L" });

        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("allow", result.Behavior);
        var answers = result.UpdatedInput!.Value.GetProperty("answers");
        Assert.Equal("Blue", answers.GetProperty("Which color?").GetString());
        Assert.Equal("S, L", answers.GetProperty("Which size?").GetString());
        Assert.Equal(2, result.UpdatedInput.Value.GetProperty("questions").GetArrayLength());
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Running, status.State);
        Assert.Null(status.PendingQuestion);
        Assert.Null(status.CurrentQuestion);
    }

    [Fact]
    public async Task SendInput_WhileASingleQuestionWaits_AnswersIt()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new[] { new { question = "Which branch?", options = new[] { new { label = "main" } }, multiSelect = false } },
        });
        var asking = harness.Projects.RequestPermissionAsync(created.Id, new PermissionPromptRequest("AskUserQuestion", input, null), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingQuestion != null);

        await harness.Projects.SendInputAsync(created.Id, "release/2.0");

        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("allow", result.Behavior);
        Assert.Equal("release/2.0", result.UpdatedInput!.Value.GetProperty("answers").GetProperty("Which branch?").GetString());
    }

    /// <summary>status.json left WaitingPermission by a server that died: no request survives it.</summary>
    [Fact]
    public async Task Recovery_OfAProjectLeftWaitingPermission_IsStopped_WithoutTheRequest()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var folder = Path.Combine(harness.RootPath, "left");
        var godMode = Path.Combine(folder, ".godmode");
        Directory.CreateDirectory(godMode);
        var now = DateTime.UtcNow;
        var left = new ProjectStatus("left", "left", ProjectState.WaitingPermission, now, now, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0,
            PendingPermission: new PendingPermission("r1", "Bash", JsonSerializer.SerializeToElement(new { command = "ls" }), "Bash: ls", now));
        File.WriteAllText(Path.Combine(godMode, "status.json"), JsonSerializer.Serialize(left, JsonDefaults.Options));

        await harness.Projects.RecoverProjectsAsync();

        var status = await harness.Projects.GetStatusAsync($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/left");
        Assert.Equal(ProjectState.Stopped, status.State);
        Assert.Null(status.PendingPermission);
    }
}
