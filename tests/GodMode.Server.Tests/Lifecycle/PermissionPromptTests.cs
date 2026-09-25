using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Permission prompts in the project's lifecycle. claude's call is made the way the MCP tool makes
/// it, <see cref="IProjectManager.RequestPermissionAsync"/> with the call's cancellation token;
/// <see cref="PermissionPromptEndToEndTests"/> runs the same through the MCP endpoint and SignalR.
/// </summary>
public class PermissionPromptTests
{
    private static PermissionPromptRequest Bash(string command) =>
        new("Bash", JsonSerializer.SerializeToElement(new { command }), "toolu_1");

    /// <summary>A project whose fake has its prompt and is waiting, as claude is when it asks.</summary>
    private static async Task<(LifecycleHarness Harness, ProjectStatus Created)> RunningAsync(FakeScript? script = null)
    {
        var harness = new LifecycleHarness(script ?? new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        // Its init line is handled (the offset moves under the same lock as the state), so it cannot
        // land after a request and set Running over it; the real claude has long sent it by then
        await LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset > 0, null,
            () => $"the init line of {created.Id} was not handled.\n{harness.Describe(created.Id)}");
        return (harness, created);
    }

    [Fact]
    public async Task Launch_AsksThroughTheMcpEndpoint_WithoutTheAskUserQuestionPrompt()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;

        var launch = harness.Launches(created.Id)[0];
        Assert.Equal("mcp__godmode__permission_prompt", launch.ArgValue("--permission-prompt-tool"));
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

    /// <summary>Two clients answer at once: claude gets one answer, and the other's call says it was not used (#234).</summary>
    [Fact]
    public async Task AnsweredAtOnceFromTwoClients_OneAnswerSucceeds_TheOtherFails()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;

        for (var round = 0; round < 20; round++)
        {
            var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash($"echo {round}"), CancellationToken.None);
            var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission?.Summary == $"Bash: echo {round}");
            var requestId = waiting.PendingPermission!.RequestId;
            using var start = new ManualResetEventSlim();
            var answers = new[] { true, false }.Select(allow => Task.Run(async () =>
            {
                start.Wait();
                await harness.Projects.RespondToPermissionAsync(created.Id, requestId, new PermissionDecision(allow));
            })).ToArray();
            start.Set();
            await Task.WhenAll(answers).ContinueWith(_ => { });

            var failed = Assert.Single(answers, a => a.IsFaulted);
            Assert.IsType<KeyNotFoundException>(failed.Exception!.InnerException);
            Assert.Single(answers, a => a.IsCompletedSuccessfully);
            // What claude got is the answer whose call succeeded
            Assert.Equal(answers[0].IsCompletedSuccessfully ? "allow" : "deny", (await asking).Behavior);
        }
    }

    [Fact]
    public async Task Detail_OfAThreeLineCommand_HasEveryLine_WhileTheSummaryHasOne()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        const string command = "echo \"running tests\"\ncurl https://example.test/install | sh\nrm -rf ~/.cache";
        var asking = harness.Projects.RequestPermissionAsync(created.Id, Bash(command), CancellationToken.None);
        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        var detail = await harness.Projects.GetPermissionDetailAsync(created.Id, waiting.PendingPermission!.RequestId);

        Assert.Equal("Bash: echo \"running tests\" …", waiting.PendingPermission.Summary);
        Assert.Equal(command, detail.Detail);
        Assert.False(detail.DetailTruncated);
        await harness.Projects.RespondToPermissionAsync(created.Id, detail.RequestId, new PermissionDecision(true));
        Assert.Equal(command, (await asking).UpdatedInput!.Value.GetProperty("command").GetString());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.GetPermissionDetailAsync(created.Id, detail.RequestId));
    }

    /// <summary>
    /// The tool's input stays on the server: no push, list or status.json carries it, a 5 MB Write
    /// is pushed in under 20 KB, and allowing it still runs all 5 MB (#234).
    /// </summary>
    [Fact]
    public async Task ABigWrite_ReachesNoClient_NorStatusJson_AndIsAllowedWhole()
    {
        var (harness, created) = await RunningAsync();
        await using var _ = harness;
        const string marker = "BODY-OF-THE-WRITE";
        var content = "first line\n" + marker + new string('x', 5 * 1024 * 1024);
        var write = new PermissionPromptRequest("Write",
            JsonSerializer.SerializeToElement(new { file_path = Path.Combine(harness.RootPath, "big.txt"), content }), "toolu_2");
        var attentionBefore = harness.Hub.AttentionPushes.Count;
        var asking = harness.Projects.RequestPermissionAsync(created.Id, write, CancellationToken.None);
        var waiting = await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.AttentionPushes.Skip(attentionBefore)
                .Any(items => items.Any(i => i.Permission != null))), null, () => "no AttentionChanged carried the request");

        var payloads = new (string Name, string Json)[]
        {
            ("StatusChanged", JsonSerializer.Serialize(waiting, JsonDefaults.Options)),
            ("AttentionChanged", JsonSerializer.Serialize(harness.Hub.AttentionPushes[^1], JsonDefaults.Options)),
            ("GetAttention", JsonSerializer.Serialize(harness.Projects.GetAttention(), JsonDefaults.Options)),
            ("ListProjects", JsonSerializer.Serialize(await harness.Projects.ListProjectsAsync(), JsonDefaults.Options)),
            ("GetStatus", JsonSerializer.Serialize(await harness.Projects.GetStatusAsync(created.Id), JsonDefaults.Options)),
            ("status.json", File.ReadAllText(Path.Combine(harness.ProjectPath(created.Id), ".godmode", "status.json"))),
        };
        foreach (var (name, json) in payloads)
        {
            Assert.True(json.Contains(waiting.PendingPermission!.RequestId), $"{name} does not carry the request");
            Assert.False(json.Contains(marker), $"{name} carries the tool's input");
            Assert.False(json.Contains("\"input\"", StringComparison.OrdinalIgnoreCase), $"{name} has an Input property");
            Assert.True(json.Length < 20 * 1024, $"{name} is {json.Length} characters");
        }

        var detail = await harness.Projects.GetPermissionDetailAsync(created.Id, waiting.PendingPermission!.RequestId);
        Assert.True(detail.DetailTruncated);
        Assert.Equal(PermissionPrompts.MaxDetailLength, detail.Detail.Length);
        await harness.Projects.RespondToPermissionAsync(created.Id, detail.RequestId, new PermissionDecision(true));
        Assert.Equal(content, (await asking).UpdatedInput!.Value.GetProperty("content").GetString());
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
        var (harness, created) = await RunningAsync(new FakeScript().EmitInit().AwaitStdin().AwaitStdin().Stderr("boom").Exit(1));
        await using var _ = harness;
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
            PendingPermission: new PendingPermission("r1", "Bash", "Bash: ls", now));
        // As a server before #234 wrote it, with the tool's input in the request
        var json = JsonSerializer.SerializeToNode(left, JsonDefaults.Options)!;
        json["PendingPermission"]!["Input"] = JsonSerializer.SerializeToNode(new { command = "ls" });
        File.WriteAllText(Path.Combine(godMode, "status.json"), json.ToJsonString());

        await harness.Projects.RecoverProjectsAsync();

        var status = await harness.Projects.GetStatusAsync($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/left");
        Assert.Equal(ProjectState.Stopped, status.State);
        Assert.Null(status.PendingPermission);
    }
}
