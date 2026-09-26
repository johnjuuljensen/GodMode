using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session that was working when the server stopped carries on by itself after the restart; one
/// waiting on the user still waits, with its question; one the user stopped, or whose root says
/// <c>resumeOnRestart: false</c>, stays Stopped.
/// </summary>
public class RestartResumeTests
{
    private const string Question = "Which branch should I use?";

    /// <summary>Waits for a prompt, reports it is working, and waits for the next one.</summary>
    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    /// <summary>Ends its turn on a question and waits for the answer.</summary>
    private static FakeScript Asking() =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant(Question).Sleep(50).EmitResult(Question).AwaitStdin();

    /// <summary>Waits until the project's claude has said it is working.</summary>
    private static Task WorkingAsync(LifecycleHarness harness, string projectId) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(projectId).Contains("Working on it")), null,
            () => $"claude did not start working.\n{harness.Describe(projectId)}");

    private static async Task<ProjectStatus> CreateWorkingAsync(LifecycleHarness harness, string name = "p1")
    {
        var created = await harness.CreateProjectAsync(name);
        await harness.WaitForStdinAsync(created.Id);
        await WorkingAsync(harness, created.Id);
        return created;
    }

    private static string Prompt(FakeLaunch launch, int line = 0) => ProjectLifecycleTests.PromptText(launch.Stdin[line]);

    [Fact]
    public async Task Running_IsResumedAfterARestart_WithTheContinuePrompt()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await CreateWorkingAsync(harness);
        var create = harness.Launches(created.Id)[0];

        harness.StopHost();
        Assert.Equal(ProjectState.Running, harness.ReadStatusFile(created.Id).StateAtShutdown);
        await harness.RestartAsync();

        var resume = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Equal(create.ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.Single(resume.Stdin);
        Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(resume));
        await WaitForOutputCountAsync(harness, created.Id, "Working on it", 2);
        var status = await harness.WaitForStateAsync(created.Id, ProjectState.Running);
        Assert.Null(status.StateAtShutdown);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
        Assert.Equal(ProjectState.Running, harness.Hub.StatusPushes(created.Id)[^1].State);
    }

    /// <summary>
    /// The resume a restart makes is the resume the user makes (#164): the same arguments,
    /// environment (the profile's) and MCP config, except the token each launch is issued afresh.
    /// </summary>
    [Fact]
    public async Task ResumeAfterARestart_LaunchesAsAResumeByTheUser()
    {
        await using var harness = new LifecycleHarness(Working(),
            profileEnvironment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "godmode-claude-config") });
        var created = await CreateWorkingAsync(harness);
        await harness.RestartAsync();
        var afterRestart = await harness.WaitForStdinAsync(created.Id, index: 1);

        await harness.Projects.StopProjectAsync(created.Id);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var byUser = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 2);

        Assert.Equal(byUser.Argv, afterRestart.Argv);
        Assert.Equal(Sorted(byUser.Environment), Sorted(afterRestart.Environment));
        Assert.Equal(GodModeMcpEntry.Of(byUser).WithoutToken(), GodModeMcpEntry.Of(afterRestart).WithoutToken());
        Assert.Equal(byUser.Environment["CLAUDE_CONFIG_DIR"], afterRestart.Environment["CLAUDE_CONFIG_DIR"]);
    }

    /// <summary>
    /// A question stays the user's to answer: the project is WaitingInput again with it, and no
    /// process is launched until a reply resumes it. Across a second restart too.
    /// </summary>
    [Fact]
    public async Task WaitingInput_IsWaitingInputAfterARestart_WithItsQuestion_AndNoProcess_UntilAnswered()
    {
        await using var harness = new LifecycleHarness(Asking());
        var created = await harness.CreateProjectAsync();
        var asked = await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);
        var before = Assert.Single(harness.Projects.GetAttention());

        await harness.RestartAsync();
        await harness.RestartAsync();

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal((ProjectState.WaitingInput, Question, asked.QuestionAt), (status.State, status.CurrentQuestion, status.QuestionAt));
        Assert.Null(status.StateAtShutdown);
        Assert.Equal(ProjectState.WaitingInput, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(ProjectState.WaitingInput, harness.Hub.StatusPushes(created.Id)[^1].State);
        Assert.Equal(before, Assert.Single(harness.Projects.GetAttention()));
        Assert.Single(harness.Launches(created.Id));

        await harness.Projects.ReplyAndResumeAsync(created.Id, "main");

        var resume = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Equal(harness.Launches(created.Id)[0].ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.Single(resume.Stdin);
        Assert.Equal("main", Prompt(resume));
    }

    /// <summary>The shutdown denied the permission prompt, so claude saw a deny: it is resumed and told to carry on, and can ask again.</summary>
    [Fact]
    public async Task WaitingPermission_IsResumedAfterARestart_WithTheContinuePrompt()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await CreateWorkingAsync(harness);
        var asking = harness.Projects.RequestPermissionAsync(created.Id,
            new PermissionPromptRequest("Bash", JsonSerializer.SerializeToElement(new { command = "ls" }), "toolu_1"), CancellationToken.None);
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingPermission);

        harness.StopHost();
        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        Assert.Equal(ProjectState.WaitingPermission, harness.ReadStatusFile(created.Id).StateAtShutdown);
        await harness.RestartAsync();

        var resume = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(resume));
        var status = await harness.WaitForStateAsync(created.Id, ProjectState.Running);
        Assert.Null(status.PendingPermission);
    }

    [Fact]
    public async Task StoppedByTheUserBeforeTheRestart_IsNotResumed()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await CreateWorkingAsync(harness);
        await harness.Projects.StopProjectAsync(created.Id);

        await harness.RestartAsync();

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
        Assert.Single(harness.Launches(created.Id));
    }

    /// <summary>An idle session finished its turn: there is nothing to carry on with.</summary>
    [Fact]
    public async Task Idle_IsNotResumed()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("All done.").AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        await harness.RestartAsync();

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
        Assert.Single(harness.Launches(created.Id));
    }

    [Fact]
    public async Task ResumeOnRestartOff_IsNotResumed_AndStaysStopped()
    {
        await using var harness = new LifecycleHarness(Working(), rootConfig: new Dictionary<string, object> { ["resumeOnRestart"] = false });
        var created = await CreateWorkingAsync(harness);

        await harness.RestartAsync();

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
        Assert.Single(harness.Launches(created.Id));
    }

    [Fact]
    public async Task ResumePrompt_FromTheRootConfig_IsWhatTheResumeIsTold()
    {
        await using var harness = new LifecycleHarness(Working(), rootConfig: new Dictionary<string, object> { ["resumePrompt"] = "Carry on, please." });
        var created = await CreateWorkingAsync(harness);

        await harness.RestartAsync();

        Assert.Equal("Carry on, please.", Prompt(await harness.WaitForStdinAsync(created.Id, index: 1)));
    }

    /// <summary>A resume the root config refuses is Error, saying why, and pushed; nothing is launched, and it is not tried again.</summary>
    [Fact]
    public async Task ResumeRefusedByTheRootConfig_IsError_AndPushed()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await CreateWorkingAsync(harness);
        harness.StopHost();
        // An overlay makes the root's actions its own: the project's "Create" is gone
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.other.json"), "{}");

        await harness.RestartAsync();

        var pushed = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Error);
        Assert.StartsWith("root config has no action 'Create'", pushed.LastError);
        Assert.Equal(ProjectState.Error, harness.ReadStatusFile(created.Id).State);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
        Assert.Single(harness.Launches(created.Id));
    }

    /// <summary>A resumed claude that exits at once is Error with its stderr, as any exit is; it is not launched again.</summary>
    [Fact]
    public async Task ResumeThatExitsAtOnce_IsError_WithItsStderr_AndIsNotRetried()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = await CreateWorkingAsync(harness);
        harness.UseScript(new FakeScript().Stderr("fatal: the session is gone").Exit(1));

        await harness.RestartAsync();

        var pushed = await harness.WaitForStatusPushAsync(created.Id, s => s.State == ProjectState.Error);
        Assert.Contains("fatal: the session is gone", pushed.LastError);
        await Task.Delay(500);
        Assert.Equal(2, harness.Launches(created.Id).Count);
        Assert.Null(harness.ReadStatusFile(created.Id).StateAtShutdown);
    }

    /// <summary>More projects than are resumed at once: every one is, in turn.</summary>
    [Fact]
    public async Task ManyProjects_AreAllResumed()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = new List<ProjectStatus>();
        for (var i = 1; i <= 5; i++) created.Add(await CreateWorkingAsync(harness, $"p{i}"));

        await harness.RestartAsync();

        foreach (var project in created)
            Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(await harness.WaitForStdinAsync(project.Id, index: 1)));
    }

    /// <summary>
    /// A resumed claude that holds its slot until the test lets it go (<see cref="LetResumesStart"/>):
    /// it starts its session then, after its first input.
    /// </summary>
    private static FakeScript HeldAtStart(LifecycleHarness harness) =>
        new FakeScript().AwaitStdin().AwaitFile(ResumesMayStart(harness)).EmitInit().AwaitStdin();

    private static string ResumesMayStart(LifecycleHarness harness) => Path.Combine(harness.RootPath, "resumes-may-start");

    private static void LetResumesStart(LifecycleHarness harness) => File.WriteAllText(ResumesMayStart(harness), "");

    /// <summary>Restarts without carrying on, then starts carrying on and waits until 3 projects have their resume launched.</summary>
    private static async Task<Task> RestartResumingAsync(LifecycleHarness harness, IReadOnlyList<ProjectStatus> projects)
    {
        harness.UseScript(HeldAtStart(harness));
        await harness.RestartAsync(resume: false);
        var resuming = harness.Projects.ResumeInterruptedProjectsAsync();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Resumed(harness, projects).Count() == 3), null,
            () => $"{Resumed(harness, projects).Count()} projects were resumed, not 3");
        return resuming;
    }

    private static IEnumerable<ProjectStatus> Resumed(LifecycleHarness harness, IEnumerable<ProjectStatus> projects) =>
        projects.Where(p => harness.Launches(p.Id).Count > 1);

    /// <summary>
    /// Three are resumed at a time, and one queued behind them is decided when its turn comes: the
    /// user stopped it meanwhile, so it is not launched.
    /// </summary>
    [Fact]
    public async Task StoppedWhileQueued_IsNotResumed_AndThreeAreResumedAtATime()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = new List<ProjectStatus>();
        for (var i = 1; i <= 4; i++) created.Add(await CreateWorkingAsync(harness, $"p{i}"));

        var resuming = await RestartResumingAsync(harness, created);
        await Task.Delay(500);
        Assert.Equal(3, Resumed(harness, created).Count());
        var queued = Assert.Single(created.Except(Resumed(harness, created)));
        await harness.Projects.StopProjectAsync(queued.Id);
        LetResumesStart(harness);
        await resuming.WaitAsync(LifecycleHarness.DefaultTimeout);

        Assert.Single(harness.Launches(queued.Id));
        var status = await harness.Projects.GetStatusAsync(queued.Id);
        Assert.Equal((ProjectState.Stopped, (ProjectState?)null), (status.State, status.StateAtShutdown));
    }

    /// <summary>
    /// A shutdown while the start is still carrying on launches nothing more: the queued projects keep
    /// their marker, those under way are stopped with theirs, no claude outlives the server, and the
    /// start after it resumes all of them.
    /// </summary>
    [Fact]
    public async Task ShutdownWhileResuming_LaunchesNoMore_AndEveryProjectKeepsItsMarker()
    {
        await using var harness = new LifecycleHarness(Working());
        var created = new List<ProjectStatus>();
        for (var i = 1; i <= 5; i++) created.Add(await CreateWorkingAsync(harness, $"p{i}"));

        var resuming = await RestartResumingAsync(harness, created);
        var queued = created.Except(Resumed(harness, created)).ToList();
        harness.StopHost();
        await resuming.WaitAsync(LifecycleHarness.DefaultTimeout);

        Assert.Equal(2, queued.Count);
        Assert.All(queued, p => Assert.Single(harness.Launches(p.Id)));
        Assert.All(created, p => Assert.Equal((ProjectState.Stopped, (ProjectState?)ProjectState.Running),
            (harness.ReadStatusFile(p.Id).State, harness.ReadStatusFile(p.Id).StateAtShutdown)));
        Assert.All(created.SelectMany(p => harness.Launches(p.Id)),
            launch => Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) outlived the server"));

        var before = created.ToDictionary(p => p.Id, p => harness.Launches(p.Id).Count);
        harness.UseScript(Working());
        await harness.RestartAsync();
        foreach (var project in created)
            Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(await harness.WaitForStdinAsync(project.Id, index: before[project.Id])));
    }

    /// <summary>
    /// The Ctrl+C gap: claude got the Ctrl+C too, and its exit was handled (Error; or Stopped with
    /// its question gone, for a clean exit while waiting) before the server's shutdown began. The
    /// shutdown takes that back: Stopped, with the question and what it was doing, and it carries on.
    /// </summary>
    [Theory]
    [InlineData(false, 1, ProjectState.Error)]
    [InlineData(true, 0, ProjectState.Stopped)]
    public async Task ExitJustBeforeTheShutdown_IsTakenBack_AndCarriesOn(bool asking, int exitCode, ProjectState afterExit)
    {
        await using var harness = new LifecycleHarness(
            (asking ? Asking() : Working()).Stderr("^C").Exit(exitCode));
        var created = asking ? await harness.CreateProjectAsync() : await CreateWorkingAsync(harness);
        var active = asking ? ProjectState.WaitingInput : ProjectState.Running;
        await harness.WaitForStateAsync(created.Id, active);

        await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(created.Id), "exit now");
        await harness.WaitForStateAsync(created.Id, afterExit);
        harness.StopHost();

        var onDisk = harness.ReadStatusFile(created.Id);
        Assert.Equal((ProjectState.Stopped, active, (string?)null), (onDisk.State, onDisk.StateAtShutdown, onDisk.LastError));
        Assert.Equal(asking ? Question : null, onDisk.CurrentQuestion);

        await harness.RestartAsync();

        if (asking)
        {
            Assert.Equal(ProjectState.WaitingInput, (await harness.Projects.GetStatusAsync(created.Id)).State);
            Assert.Single(harness.Launches(created.Id));
        }
        else
            Assert.Equal(CreateAction.DefaultResumePrompt, Prompt(await harness.WaitForStdinAsync(created.Id, index: 1)));
    }

    /// <summary>A crash well before the shutdown is a crash: it stays Error and is not resumed.</summary>
    [Fact]
    public async Task ExitLongBeforeTheShutdown_StaysError()
    {
        await using var harness = new LifecycleHarness(Working().Stderr("fatal: something broke").Exit(1),
            settings: new Dictionary<string, string?> { [ProjectManager.ExitBeforeShutdownWindowSetting] = "0" });
        var created = await CreateWorkingAsync(harness);
        await harness.ProcessManager.SendInputAsync(harness.ProjectInfo(created.Id), "exit now");
        await harness.WaitForStateAsync(created.Id, ProjectState.Error);

        await harness.RestartAsync();

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Error, status.State);
        Assert.Contains("fatal: something broke", status.LastError);
        Assert.Null(status.StateAtShutdown);
        Assert.Single(harness.Launches(created.Id));
    }

    private static Task WaitForOutputCountAsync(LifecycleHarness harness, string projectId, string text, int count) =>
        LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.ReadOutputFile(projectId).Split(text).Length - 1 >= count), null,
            () => $"output.jsonl does not have \"{text}\" {count} times.\n{harness.Describe(projectId)}");

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> environment) =>
        new(environment.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);
}
