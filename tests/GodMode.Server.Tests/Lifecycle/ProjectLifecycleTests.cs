using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
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

    /// <summary>
    /// Server shutdown kills claude and leaves the project Stopped on disk, so recovery on the next
    /// start does not run a second process on the session while an orphan still has it.
    /// </summary>
    [Fact]
    public async Task StoppingTheHost_KillsTheProcess_AndPersistsStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);
        Assert.Equal(launch.Pid, harness.ProjectInfo(created.Id).Process.ProcessId);

        harness.StopHost();

        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after the host stopped");
        Assert.Equal(ProjectState.Stopped, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(ProjectState.Stopped, harness.Hub.StatusPushes(created.Id)[^1].State);
    }

    /// <summary>
    /// A Ctrl+C on a server run in a terminal reaches claude too, which can die before the server's
    /// shutdown handler runs. Here the fake exits 1 by itself as the host stops, and its exit is held
    /// (the test takes the state lock) until the server's handler has begun. It is Stopped, question
    /// kept, on disk by the time the host has stopped, not Error.
    /// </summary>
    [Fact]
    public async Task ProcessExitingByItselfAsTheHostStops_IsStopped_WithItsQuestion()
    {
        const string question = "Which branch should I use?";
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().EmitAssistant(question).EmitResult().AwaitStdin().Exit(1));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);
        var project = harness.ProjectInfo(created.Id);
        var pid = project.Process.ProcessId;
        // Every wait is bounded: the host runs this synchronously, so an unbounded one would hang the
        // suite. A failure is kept for after StopHost, which logs and swallows a callback's exception
        string? stopping = null;
        harness.OnHostStopping(() =>
        {
            if (!project.Process.StateLock.Wait(LifecycleHarness.DefaultTimeout))
            {
                stopping = "the state lock was not free within the timeout";
                return;
            }
            try
            {
                // Found before it is told to exit: once it has, there is no process by its pid to find
                using var fake = System.Diagnostics.Process.GetProcessById(pid);
                if (!harness.ProcessManager.SendInputAsync(project, "exit now").Wait(LifecycleHarness.DefaultTimeout))
                    stopping = "sending the fake its input did not finish within the timeout";
                else if (!fake.WaitForExit(LifecycleHarness.DefaultTimeout))
                    stopping = $"the fake (pid {pid}) did not exit within the timeout";
            }
            catch (Exception ex)
            {
                stopping = $"the host-stopping callback threw: {ex}";
            }
            finally
            {
                _ = Task.Delay(200).ContinueWith(_ => project.Process.StateLock.Release());
            }
        });

        harness.StopHost();
        Assert.True(stopping == null, $"{stopping}.\n{harness.Describe(created.Id)}");

        Assert.Equal(1, (await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode != null)).ExitCode);
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal(ProjectState.Stopped, onDisk.State);
        Assert.Equal(question, onDisk.CurrentQuestion);
        Assert.Null(onDisk.LastError);
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id), s => s.State == ProjectState.Error);
    }

    [Fact]
    public async Task ProcessExitsWithAnError_IsError_AndPushesItsStderr()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().Stderr("fatal: something broke").Exit(1));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 1);
        var status = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Error);
        Assert.Contains("fatal: something broke", status.LastError);
        Assert.Equal(ProjectState.Error, (await harness.Projects.GetStatusAsync(created.Id)).State);
        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal(ProjectState.Error, onDisk.State);
        Assert.Contains("fatal: something broke", onDisk.LastError);
        Assert.Equal(0, harness.ProjectInfo(created.Id).Process.ProcessId);
    }

    [Fact]
    public async Task ProcessExitsCleanlyAfterAResult_IsStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done.").Exit(0));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 0);
        var status = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Stopped);
        Assert.Null(status.LastError);
        Assert.Equal(ProjectState.Stopped, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(0, harness.ProjectInfo(created.Id).Process.ProcessId);
    }

    /// <summary>A clean exit in the middle of a turn, before its result, is not a finished session.</summary>
    [Fact]
    public async Task ProcessExitsCleanlyBeforeAResult_IsError()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working").Exit(0));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 0);
        var status = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Error);
        Assert.NotNull(status.LastError);
    }

    /// <summary>Nobody subscribes to the project's output; every client still hears its state change.</summary>
    [Fact]
    public async Task AssistantThenResult_PushesStatusChanged_WithoutASubscriber()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done."));

        var created = await harness.CreateProjectAsync();

        var status = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Idle);
        Assert.Equal(1, status.Metrics.InputTokens);
    }

    [Fact]
    public async Task ErrorResult_IsError_WithItsText()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Trying").EmitResult("API overloaded", isError: true));

        var created = await harness.CreateProjectAsync();

        var status = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Error);
        Assert.Equal("API overloaded", status.LastError);
    }

    /// <summary>A stderr line starting "Error:" is shown, not taken for the session failing.</summary>
    [Fact]
    public async Task StderrErrorLine_DoesNotMakeTheProjectError()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin()
            .Stderr("Error: a hook failed").Sleep(100).EmitAssistant("Done anyway.").EmitResult());

        var created = await harness.CreateProjectAsync();

        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        Assert.Contains("a hook failed", harness.ReadOutputFile(created.Id));
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id), s => s.State == ProjectState.Error);
    }

    /// <summary>Only system/init means a session (re)started; other system events leave the state alone.</summary>
    [Fact]
    public async Task SystemEventOtherThanInit_LeavesTheStateAlone()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("Done.")
            .Emit("""{"type":"system","subtype":"compact_boundary"}"""));

        var created = await harness.CreateProjectAsync();

        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.Hub.Pushes.Any(p => p.RawJson?.Contains("compact_boundary") == true)), null,
            () => $"the compact_boundary line was never broadcast.\n{harness.Describe(created.Id)}");
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    /// <summary>
    /// A result still queued when Stop kills the process is handled before Stopped is applied, so it
    /// cannot turn Stopped back into Idle. The consumer is held on the assistant line's broadcast
    /// while the result waits behind it.
    /// </summary>
    [Fact]
    public async Task ResultQueuedWhenStopped_StaysStopped()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().Sleep(100)
            .EmitAssistant("Almost").EmitResult("late"));
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);
        // Hold only once init is broadcast: holding it would keep "Almost" out of output.jsonl
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.Pushes.Any(p => p.RawJson?.Contains("\"init\"") == true)), null,
            () => $"the init line was never broadcast.\n{harness.Describe(created.Id)}");
        harness.Hub.HoldOutput();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(created.Id).Contains("Almost")), null,
            () => $"the assistant line never reached output.jsonl.\n{harness.Describe(created.Id)}");

        var stop = harness.Projects.StopProjectAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(!LifecycleHarness.IsProcessAlive(launch.Pid)), null,
            () => $"fake claude (pid {launch.Pid}) is still running after Stop");
        harness.Hub.ReleaseOutput();
        await stop;
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.Hub.Pushes.Any(p => p.RawJson?.Contains("\"late\"") == true)), null,
            () => $"the queued result was never handled.\n{harness.Describe(created.Id)}");

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Equal(ProjectState.Stopped, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(ProjectState.Stopped, harness.Hub.StatusPushes(created.Id)[^1].State);
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

    /// <summary>
    /// A delete that its script refuses (godmode-dev's refuses with uncommitted changes) leaves the
    /// project as it was: resumed, its output is still persisted and still moves its state on.
    /// </summary>
    [Fact]
    public async Task DeleteRefusedByItsScript_ThenResume_StillHandlesOutput()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."),
            rootConfig: new Dictionary<string, object> { ["delete"] = "refuse.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "refuse.ps1"), "exit 1");
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.DeleteProjectAsync(created.Id));
        harness.UseScript(new FakeScript().EmitInit().EmitAssistant("Committed.").EmitResult());
        await harness.Projects.ResumeProjectAsync(created.Id);

        await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.ReadOutputFile(created.Id).Contains("Committed.")), null,
            () => $"the resumed launch's output is not in output.jsonl.\n{harness.Describe(created.Id)}");
    }

    /// <summary>
    /// A resume claude has no conversation for exits at once; the server starts a fresh session on
    /// the same id instead, with the MCP config the resume was given, and never shows Error.
    /// </summary>
    [Fact]
    public async Task ResumeOfAnUnknownSession_StartsFresh_WithItsMcpConfig()
    {
        await using var harness = new LifecycleHarness(new FakeScript().RejectResume().EmitInit().Turn("First."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var sessionId = (await harness.WaitForStdinAsync(created.Id)).ArgValue("--session-id");
        await harness.Projects.StopProjectAsync(created.Id);
        var configPath = McpConfigFile.PathFor(harness.ProjectPath(created.Id));
        var pushedBefore = harness.Hub.StatusPushes(created.Id).Count;

        await harness.Projects.ResumeProjectAsync(created.Id);

        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 1 && l.ArgValue("--resume") == sessionId, index: 1);
        var fresh = await harness.WaitForStdinAsync(created.Id, index: 2);
        Assert.Equal(sessionId, fresh.ArgValue("--session-id"));
        Assert.StartsWith("Continue from where we left off", PromptText(Assert.Single(fresh.Stdin)));
        // Idle from the bare resume first; the fresh session's turn then runs, and ends Idle again
        var running = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Running, skip: pushedBefore);
        await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Idle,
            skip: harness.Hub.StatusPushes(created.Id).ToList().LastIndexOf(running) + 1);
        Assert.True(File.Exists(configPath), $"{configPath} should exist while the fresh session runs.\n{harness.Describe(created.Id)}");
        Assert.Equal(fresh.Pid, harness.ProjectInfo(created.Id).Process.ProcessId);
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id), s => s.State == ProjectState.Error);
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);

        await harness.Projects.StopProjectAsync(created.Id);
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(!File.Exists(configPath))),
            $"{configPath} is still there after the fresh session exited.\n{harness.Describe(created.Id)}");
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
    internal static string PromptText(string stdinLine)
    {
        using var message = JsonDocument.Parse(stdinLine);
        Assert.Equal("user", message.RootElement.GetProperty("type").GetString());
        return message.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
