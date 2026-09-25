using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A restart, a stop during a shutdown, a save or an append that fails, a slow client, or a turn's
/// result handled after a send leaves each project in the state it is really in (#238).
/// </summary>
public class TrueStateTests
{
    private const string Question = "Which color?";

    private static readonly object AskUserQuestionInput = new
    {
        questions = new object[]
        {
            new { question = Question, header = "Color", options = new[] { new { label = "Red" }, new { label = "Blue" } }, multiSelect = false },
        },
    };

    /// <summary>Waits for a prompt, reports it is working, and waits for the next one.</summary>
    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    private static Task OutputContainsAsync(LifecycleHarness harness, string projectId, string text) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(projectId).Contains(text)), null,
            () => $"output.jsonl never had \"{text}\".\n{harness.Describe(projectId)}");

    private static string Prompt(FakeLaunch launch, int line = 0) => ProjectLifecycleTests.PromptText(launch.Stdin[line]);

    // ── Shutdown ──

    /// <summary>
    /// claude asks an AskUserQuestion through the permission prompt, and the host stops: the question
    /// is still the user's after the restart. The project is WaitingInput with it, and claude is not
    /// launched, let alone told to carry on, until the user answers.
    /// </summary>
    [Fact]
    public async Task AskUserQuestion_WaitingWhenTheHostStops_IsStillAskedAfterTheRestart_AndNotResumed()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin()
            .AskPermission("AskUserQuestion", AskUserQuestionInput, "toolu_q").AwaitStdin(), mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingQuestion != null);

        harness.StopHost();

        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal((ProjectState.Stopped, (ProjectState?)ProjectState.WaitingInput, Question),
            (onDisk.State, onDisk.StateAtShutdown, onDisk.CurrentQuestion));
        Assert.Null(onDisk.PendingQuestion);

        await harness.RestartAsync();

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal((ProjectState.WaitingInput, Question), (status.State, status.CurrentQuestion));
        Assert.Single(harness.Launches(created.Id));
        Assert.Equal(AttentionKind.Question, Assert.Single(harness.Projects.GetAttention()).Kind);
    }

    /// <summary>
    /// claude answers the shutdown's interrupt with an <c>error_during_execution</c> result, as the
    /// real CLI does, and it is handled after the shutdown began: the marker written first stands,
    /// and the restart carries on with the project.
    /// </summary>
    [Fact]
    public async Task InterruptAnsweredWithAnErrorResult_DuringTheShutdown_IsStillResumed()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await harness.CreateProjectAsync();
        await OutputContainsAsync(harness, created.Id, "Working on it");

        harness.StopHost();

        Assert.Contains("error_during_execution", harness.ReadOutputFile(created.Id));
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal((ProjectState.Stopped, (ProjectState?)ProjectState.Running, (string?)null),
            (onDisk.State, onDisk.StateAtShutdown, onDisk.LastError));

        await harness.RestartAsync();

        Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(await harness.WaitForStdinAsync(created.Id, index: 1)));
    }

    /// <summary>
    /// The user's Stop is in its grace period (claude ignores the interrupt) when the server shuts
    /// down. The shutdown's own stop does not take the resume lock, and before, it wrote the project's
    /// marker from its Running: the restart resumed a project the user stopped. It is not marked now.
    /// </summary>
    [Fact]
    public async Task ShutdownDuringTheUsersStop_IsNotResumed()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().IgnoreInterrupt().EmitAssistant("Working on it").Sleep(60_000),
            settings: new Dictionary<string, string?> { [ClaudeProcessManager.StopGracePeriodSetting] = "3" });
        var created = await harness.CreateProjectAsync();
        await OutputContainsAsync(harness, created.Id, "Working on it");

        var stop = harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForLaunchAsync(created.Id, l => l.Interrupts.Count > 0);
        harness.StopHost();
        await stop.WaitAsync(LifecycleHarness.DefaultTimeout);

        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal((ProjectState.Stopped, (ProjectState?)null), (onDisk.State, onDisk.StateAtShutdown));
        await harness.RestartAsync();
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Single(harness.Launches(created.Id));
    }

    // ── Saves that fail ──

    /// <summary>
    /// The save of a turn's end fails (status.json cannot be replaced): the change is pushed all the
    /// same, and status.json has it once the next change is saved.
    /// </summary>
    [Fact]
    public async Task SaveOfAResultThatFails_IsStillPushed_AndSavedWithTheNextChange()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done.").AwaitStdin());
        harness.StatusSaves.FailNext(s => s.State == ProjectState.Idle);

        var created = await harness.CreateProjectAsync();

        await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Idle);
        Assert.Equal(1, harness.StatusSaves.Failed);
        Assert.Equal(ProjectState.Running, harness.ReadStatusFile(created.Id).State);
        Assert.Contains(harness.Warnings, w => w.Contains("Could not save the status"));
        Assert.Contains(harness.Hub.AttentionPushes, list => list.Any(i => i.ProjectId == created.Id && i.Kind == AttentionKind.Finished));

        await harness.Projects.MarkSeenAsync(created.Id);

        Assert.Equal(ProjectState.Idle, harness.ReadStatusFile(created.Id).State);
    }

    /// <summary>
    /// The save of a reply's resume fails: the resume stands, is pushed, and the reply completes
    /// once claude starts its session, where before it threw and launched nothing.
    /// </summary>
    [Fact]
    public async Task ReplyWhoseResumeCannotBeSaved_StillCompletesOnInit()
    {
        await using var harness = new LifecycleHarness(new FakeScript().AwaitStdin().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        var pushed = harness.Hub.StatusPushes(created.Id).Count;
        harness.StatusSaves.FailNext(s => s.State == ProjectState.Running);

        await harness.Projects.ReplyAndResumeAsync(created.Id, "go on").WaitAsync(LifecycleHarness.DefaultTimeout);

        Assert.Equal(1, harness.StatusSaves.Failed);
        Assert.Equal("go on", Prompt(await harness.WaitForStdinAsync(created.Id, index: 1)));
        Assert.Contains(harness.Hub.StatusPushes(created.Id).Skip(pushed), s => s.State == ProjectState.Running);
    }

    // ── Permission prompts ──

    /// <summary>
    /// A permission prompt left listed after claude stopped waiting on it (a registration that failed
    /// half-way) is withdrawn by the turn's result: the project is Idle without it, and the next reply
    /// reaches claude instead of being taken for its answer.
    /// </summary>
    [Fact]
    public async Task PromptLeftListed_IsWithdrawnByTheResult_AndTheNextReplyReachesClaude()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it")
            .AwaitStdin().EmitResult().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await OutputContainsAsync(harness, created.Id, "Working on it");
        var project = harness.ProjectInfo(created.Id);
        var leaked = PermissionPrompts.Create(
            new PermissionPromptRequest("Bash", JsonSerializer.SerializeToElement(new { command = "ls" }), "toolu_leak"), project.ProjectPath, DateTime.UtcNow);
        project.Process.AddPending(leaked);
        await harness.Lifecycle.ShowPendingAsync(project);
        Assert.Equal(ProjectState.WaitingPermission, (await harness.Projects.GetStatusAsync(created.Id)).State);

        // The turn ends, with nothing waiting on the prompt
        await harness.ProcessManager.SendInputAsync(project, "finish");
        var idle = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        Assert.Null(idle.PendingPermission);
        Assert.Equal("deny", (await leaked.Completion.Task.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        await harness.Projects.SendInputAsync(created.Id, "and now the docs");
        Assert.Equal("and now the docs", Prompt(await harness.WaitForStdinAsync(created.Id, count: 3), 2));
    }

    // ── The consumer ──

    /// <summary>
    /// An append to output.jsonl fails half-way, and so does closing the file. The line is written
    /// again where the last whole one ended, every line after it is persisted and broadcast with its
    /// offset in the file, and a subscribe and a Stop still return.
    /// </summary>
    [Fact]
    public async Task AppendThatFails_KeepsTheFilesOffsets_AndSubscribeAndStopStillReturn()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("one")
            .AwaitStdin().EmitAssistant("two").EmitAssistant("three").EmitResult("done").AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await OutputContainsAsync(harness, created.Id, "one");
        var live = harness.Connect("live");
        await live.SubscribeAsync(created.Id, 0).WaitAsync(LifecycleHarness.DefaultTimeout);
        var failing = new FailingAppend();
        harness.Lifecycle.WrapOutput = failing.Wrap;

        await harness.Projects.SendInputAsync(created.Id, "go on");
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        harness.Lifecycle.WrapOutput = null;

        Assert.True(failing.Failed, "the append never failed");
        var lines = FileLines(harness.ReadOutputFile(created.Id));
        Assert.All(lines, l => JsonDocument.Parse(l.Json).Dispose());
        string[] written = ["\"text\":\"one\"", "\"text\":\"two\"", "\"text\":\"three\"", "\"result\":\"done\""];
        Assert.All(written, text => Assert.Single(lines, l => l.Json.Contains(text)));
        var broadcast = live.Received.Where(p => p.Method == nameof(IProjectHubClient.OutputReceived)).ToArray();
        foreach (var text in written[1..])
            Assert.Equal(lines.Single(l => l.Json.Contains(text)).Offset, broadcast.Single(p => p.RawJson!.Contains(text)).Offset);

        var late = harness.Connect("late");
        await late.SubscribeAsync(created.Id, 0).WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal(lines.Select(l => l.Json), late.Received.Where(p => p.Method == nameof(IProjectHubClient.OutputBatch))
            .SelectMany(p => p.Lines!).Select(l => l.RawJson));
        await harness.Projects.StopProjectAsync(created.Id).WaitAsync(LifecycleHarness.DefaultTimeout);
    }

    /// <summary>Each line of output.jsonl with the offset after it.</summary>
    private static List<(string Json, long Offset)> FileLines(string content)
    {
        var lines = new List<(string, long)>();
        long offset = 0;
        foreach (var line in content.Split('\n')[..^1])
        {
            offset += Encoding.UTF8.GetByteCount(line) + 1;
            lines.Add((line, offset));
        }
        return lines;
    }

    // ── Broadcasting ──

    /// <summary>
    /// A client that stops reading is subscribed to one busy project and, as every client, gets every
    /// status. Another project's status still reaches a client that reads within a second, through
    /// two turns of its own: nothing waits on the paused client's pushes but its own.
    /// </summary>
    [Fact]
    public async Task PausedConnection_DoesNotDelayAnotherProjectsStatus()
    {
        var script = new FakeScript().EmitInit().Turn("ready").AwaitStdin();
        for (var i = 0; i < 40; i++) script.EmitAssistant($"line {i}");
        await using var harness = new LifecycleHarness(script.EmitResult("first").Turn("second").AwaitStdin());
        var busy = await harness.CreateProjectAsync("busy");
        var other = await harness.CreateProjectAsync("other");
        await harness.WaitForStateAsync(busy.Id, ProjectState.Idle);
        await harness.WaitForStateAsync(other.Id, ProjectState.Idle);
        var slow = harness.Connect("slow");
        await slow.SubscribeAsync(busy.Id, 0);
        var fast = harness.Connect("fast");
        harness.Hub.Pause(slow.ConnectionId);
        try
        {
            // Straight to stdin: the project's own consumer is what is watched
            await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(busy.Id), "go");
            await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(other.Id), "go");
            await OutputContainsAsync(harness, other.Id, "\"first\"");
            await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(other.Id), "again");

            var ended = Stopwatch.StartNew();
            await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(other.Id).Split("\"type\":\"result\"").Length - 1 >= 3),
                null, () => $"the second turn never ended.\n{harness.Describe(other.Id)}");
            ended.Restart();
            await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(fast.Received.Any(p =>
                    p.Method == nameof(IProjectHubClient.StatusChanged) && p.ProjectId == other.Id && p.Status!.LastResult == "done")),
                TimeSpan.FromSeconds(5), () => $"the second turn's status never reached the client that reads.\n{harness.Describe(other.Id)}");
            Assert.True(ended.Elapsed < TimeSpan.FromSeconds(1), $"the status took {ended.Elapsed} to arrive");
            Assert.True(harness.Hub.Waiting(slow.ConnectionId) > 40, "the paused client should have the busy project's output waiting");
        }
        finally
        {
            harness.Hub.Resume(slow.ConnectionId);
        }
        Assert.Contains(slow.Received, p => p.RawJson?.Contains("line 39") == true);
    }

    // ── A send in the middle of a turn ──

    /// <summary>
    /// The user writes while claude works; the turn's result is handled after the send, and claude
    /// takes the message as its next turn, echoing it. Before, the result's Idle stood through the
    /// whole of that turn; the echo makes it Running again.
    /// </summary>
    [Fact]
    public async Task EchoOfAMessage_AfterAnEarlierTurnsResult_IsRunning()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("one")
            .AwaitStdin().EmitResult("turn one").EmitUser("and the docs", echo: true).Sleep(1_500)
            .EmitAssistant("two").EmitResult("turn two").AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await OutputContainsAsync(harness, created.Id, "one");

        await harness.Projects.SendInputAsync(created.Id, "and the docs");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.Pushes.Any(p => p.RawJson?.Contains("isReplay") == true)), null,
            () => $"the echo was never broadcast.\n{harness.Describe(created.Id)}");

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal((ProjectState.Running, "turn one"), (status.State, status.LastResult));
        Assert.Equal(ProjectState.Running, harness.Hub.StatusPushes(created.Id)[^1].State);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
    }

    // ── Invocations side by side ──

    /// <summary>
    /// A connection's calls run side by side now (<see cref="Hubs.ProjectHub.ParallelInvocationsPerClient"/>).
    /// Two subscribes it makes at once still replay one after the other, in the order made, each in
    /// order, as #162's subscribe does with one call at a time.
    /// </summary>
    [Fact]
    public async Task TwoSubscribesFromOneConnection_AtOnce_ReplayOneAfterTheOther()
    {
        var script = new FakeScript().EmitInit().AwaitStdin();
        for (var i = 0; i < 2_500; i++) script.EmitAssistant($"line {i}");
        await using var harness = new LifecycleHarness(script.EmitResult().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var connection = harness.Connect("c1");

        var first = connection.SubscribeAsync(created.Id, 0, "s1");
        var second = connection.SubscribeAsync(created.Id, 0, "s2");
        await Task.WhenAll(first, second).WaitAsync(LifecycleHarness.DefaultTimeout);

        var replies = connection.Received
            .Where(p => p.Method is nameof(IProjectHubClient.OutputBatch) or nameof(IProjectHubClient.OutputReplayComplete))
            .ToArray();
        Assert.Equal(["s1", "s2"], replies.Select(p => p.SubscriptionId).Distinct());
        var firstOfSecond = Array.FindIndex(replies, p => p.SubscriptionId == "s2");
        Assert.All(replies[..firstOfSecond], p => Assert.Equal("s1", p.SubscriptionId));
        Assert.Equal(nameof(IProjectHubClient.OutputReplayComplete), replies[firstOfSecond - 1].Method);
        foreach (var subscription in new[] { "s1", "s2" })
        {
            var lines = replies.Where(p => p.SubscriptionId == subscription && p.Lines != null).SelectMany(p => p.Lines!).ToArray();
            Assert.True(lines.Length > OutputLog.MaxBatchLines * 2, $"{subscription} replayed {lines.Length} lines");
            Assert.Equal(lines.Select(l => l.Offset).Order(), lines.Select(l => l.Offset));
        }
    }
}
