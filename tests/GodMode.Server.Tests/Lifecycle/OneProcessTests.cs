using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A project has at most one claude at a time: a create onto a tracked project is refused, launches
/// and stops of one project come one at a time, a delete leaves nothing waiting, and every launch
/// that does not start says why (#237).
/// </summary>
public class OneProcessTests
{
    private const string Reply = "carry on with the tests";

    /// <summary>A claude between turns: it has read its prompt, started its session, and waits.</summary>
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>A running project whose init line is in output.jsonl, which then stays as it is.</summary>
    private static async Task<(ProjectStatus Created, FakeLaunch Launch, string Output)> RunningAsync(LifecycleHarness harness, string name = "p1")
    {
        var created = await harness.CreateProjectAsync(name);
        var launch = await harness.WaitForStdinAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset > 0, null,
            () => $"the init line of {created.Id} was not handled.\n{harness.Describe(created.Id)}");
        return (created, launch, harness.ReadOutputFile(created.Id));
    }

    /// <summary>The running project is as it was: its claude, its process id, its state and its output.</summary>
    private static async Task AssertUntouchedAsync(LifecycleHarness harness, ProjectStatus created, FakeLaunch launch, string output)
    {
        Assert.True(LifecycleHarness.IsProcessAlive(launch.Pid), $"the running fake (pid {launch.Pid}) was stopped.\n{harness.Describe(created.Id)}");
        Assert.Single(harness.Launches(created.Id));
        Assert.Equal(launch.Pid, harness.ProjectInfo(created.Id).Process.ProcessId);
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Equal(output, harness.ReadOutputFile(created.Id));
    }

    [Fact]
    public async Task CreateReusingARunningProjectsFolder_IsInUse_AndLeavesItAsItWas()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var (created, launch, output) = await RunningAsync(harness);

        var refused = await Assert.ThrowsAsync<ProjectInUseException>(() =>
            harness.CreateProjectAsync(inputs: new Dictionary<string, object> { ["__reuseExisting"] = true }));

        Assert.Contains("is in use", refused.Message);
        await AssertUntouchedAsync(harness, created, launch, output);
        Assert.Single(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>On Windows "Fix" is the folder "fix": its project is in use for "Fix" too.</summary>
    [Fact]
    public async Task CreateReusingARunningProjectsFolder_InAnotherCase_IsInUse_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new LifecycleHarness(Waiting());
        var (created, launch, output) = await RunningAsync(harness, "fix");

        await Assert.ThrowsAsync<ProjectInUseException>(() =>
            harness.CreateProjectAsync("Fix", inputs: new Dictionary<string, object> { ["__reuseExisting"] = true }));

        await AssertUntouchedAsync(harness, created, launch, output);
    }

    /// <summary>
    /// A create script that puts its project in a running project's folder (godmode-dev's issue
    /// script, for an issue created twice) is refused before it is registered: the create is Error,
    /// saying why, and the running project keeps its claude and its files.
    /// </summary>
    [Fact]
    public async Task CreateWhoseScriptReturnsARunningProjectsFolder_IsInUse_AndLeavesItAsItWas()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["create"] = "claim.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "claim.ps1"),
            "if ($env:GODMODE_INPUT_TARGET) { \"project_path=$env:GODMODE_INPUT_TARGET\" | Set-Content $env:GODMODE_RESULT_FILE }");
        var (created, launch, output) = await RunningAsync(harness);

        var refused = await Assert.ThrowsAsync<ProjectInUseException>(() =>
            harness.CreateProjectAsync("p2", inputs: new Dictionary<string, object> { ["target"] = harness.ProjectPath(created.Id) }));

        Assert.Contains("is in use", refused.Message);
        await AssertUntouchedAsync(harness, created, launch, output);
        var failed = await harness.Projects.GetStatusAsync($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p2");
        Assert.Equal(ProjectState.Error, failed.State);
        Assert.Contains("is in use", failed.LastError);
    }

    /// <summary>
    /// A reply and a Resume click at once on a stopped project: one claude is started, the reply
    /// reaches it, and the project is Running with it. Each trial is a project of its own.
    /// </summary>
    [Fact]
    public async Task ResumeAndReplyAtOnce_OnAStoppedProject_StartOneClaude_WithTheReply()
    {
        const int trials = 5;
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        for (var i = 1; i <= trials; i++)
        {
            harness.UseScript(new FakeScript().EmitInit().Turn("First."));
            var created = await harness.CreateProjectAsync($"p{i}");
            await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
            await harness.Projects.StopProjectAsync(created.Id);
            // As the real CLI does: nothing until its first input, then it waits for the next
            harness.UseScript(new FakeScript().AwaitStdin().EmitInit().AwaitStdin());

            await Task.WhenAll(
                Task.Run(() => harness.Projects.ResumeProjectAsync(created.Id)),
                Task.Run(() => harness.Projects.ReplyAndResumeAsync(created.Id, Reply)));

            // A fake records itself once it runs, which can be after the calls return
            var resumed = await harness.WaitForLaunchAsync(created.Id, l => l.Stdin.Count > 0, index: 1);
            await Task.Delay(300);
            var launches = harness.Launches(created.Id);
            Assert.True(launches.Count == 2, $"trial {i}: {launches.Count - 1} claude(s) resumed, not one.\n{harness.Describe(created.Id)}");
            Assert.Single(harness.Launches(created.Id)[1].Stdin, line => line.Contains(JsonSerializer.Serialize(Reply)));
            var status = await harness.WaitForStateAsync(created.Id, ProjectState.Running);
            Assert.Equal(resumed.Pid, harness.Tracked(created.Id).Process.ProcessId);
            Assert.True(LifecycleHarness.IsProcessAlive(resumed.Pid));
            Assert.Null(status.LastError);
        }
    }

    /// <summary>
    /// A Stop while a resume is launching claude comes after the launch, not in the middle of it: the
    /// project ends Stopped, and no claude is left running.
    /// </summary>
    [Fact]
    public async Task StopDuringAResumesLaunch_IsStopped_WithNoClaudeLeft()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        var hold = harness.HoldNextLaunch();

        var resume = Task.Run(() => harness.Projects.ResumeProjectAsync(created.Id));
        await hold.Reached.WaitAsync(LifecycleHarness.DefaultTimeout);
        var stop = Task.Run(() => harness.Projects.StopProjectAsync(created.Id));
        await Task.Delay(300);
        hold.Release();
        await Task.WhenAll(resume, stop).WaitAsync(LifecycleHarness.DefaultTimeout);

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Stopped, status.State);
        Assert.Equal(ProjectState.Stopped, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(0, harness.Tracked(created.Id).Process.ProcessId);
        Assert.All(harness.Launches(created.Id), launch =>
            Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running.\n{harness.Describe(created.Id)}"));
        await Task.Delay(300);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    /// <summary>
    /// A delete its script refuses (godmode-dev's refuses with uncommitted changes) of a project
    /// waiting on a permission prompt: claude is stopped and the prompt denied before the script
    /// runs, the project is Stopped with nothing pending, and a restart does not resume it.
    /// </summary>
    [Fact]
    public async Task DeleteRefusedByItsScript_WithAPendingPermission_IsStopped_WithNothingPending_AndNotResumed()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["delete"] = "refuse.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "refuse.ps1"), "exit 1");
        var (created, launch, _) = await RunningAsync(harness);
        var asking = harness.Projects.RequestPermissionAsync(created.Id,
            new PermissionPromptRequest("Bash", JsonSerializer.SerializeToElement(new { command = "rm -rf build" }), "toolu_1"), CancellationToken.None);
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingPermission);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.DeleteProjectAsync(created.Id));

        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) outlived the delete.\n{harness.Describe(created.Id)}");
        foreach (var status in new[] { await harness.Projects.GetStatusAsync(created.Id), harness.ReadStatusFile(created.Id), harness.Hub.StatusPushes(created.Id)[^1] })
        {
            Assert.Equal(ProjectState.Stopped, status.State);
            Assert.Null(status.PendingPermission);
            Assert.Null(status.CurrentQuestion);
            Assert.Null(status.StateAtShutdown);
        }
        await Task.Delay(300);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);

        await harness.RestartAsync();

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Single(harness.Launches(created.Id));
    }

    /// <summary>
    /// A create that failed in a root whose scripts make the folder (godmode-dev's) leaves an Error
    /// project with no folder, and no .godmode to keep its status in. It can still be deleted, and
    /// then created again.
    /// </summary>
    [Fact]
    public async Task CreateThatFailedBeforeItsScriptMadeTheFolder_CanBeDeleted_AndCreatedAgain()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "make.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "make.ps1"),
            "if ($env:GODMODE_INPUT_FAIL) { exit 1 }\nNew-Item -ItemType Directory -Force $env:GODMODE_PROJECT_PATH | Out-Null");
        var projectId = $"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p1";
        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync(inputs: new Dictionary<string, object> { ["fail"] = true }));
        Assert.Equal(ProjectState.Error, (await harness.Projects.GetStatusAsync(projectId)).State);
        Assert.False(Directory.Exists(harness.ProjectPath(projectId)));

        await harness.Projects.DeleteProjectAsync(projectId).WaitAsync(LifecycleHarness.DefaultTimeout);

        Assert.DoesNotContain(await harness.Projects.ListProjectsAsync(), p => p.Id == projectId);
        var created = await harness.CreateProjectAsync();
        Assert.Equal(projectId, created.Id);
        await harness.WaitForStdinAsync(created.Id);
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(projectId)).State);
    }

    /// <summary>
    /// A resume whose claim cannot be saved (status.json cannot be replaced) launches all the same
    /// (#238: a save that fails does not fail the change), leaves no launch in flight behind it, and
    /// what it claimed is saved with the next change once status.json can be written again.
    /// </summary>
    [Fact]
    public async Task ResumeWhoseClaimCannotBeSaved_StillLaunches_AndIsSavedWithTheNextChange()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        var statusPath = Path.Combine(harness.ProjectPath(created.Id), ".godmode", "status.json");
        File.Delete(statusPath);
        Directory.CreateDirectory(statusPath);

        await harness.Projects.ResumeProjectAsync(created.Id).WaitAsync(LifecycleHarness.DefaultTimeout);

        var resumed = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        Assert.Equal(resumed.Pid, harness.Tracked(created.Id).Process.ProcessId);
        Assert.False(harness.Tracked(created.Id).Process.Launching);
        Assert.Contains(harness.Warnings, w => w.Contains("Could not save the status"));
        Directory.Delete(statusPath);

        await harness.Projects.MarkSeenAsync(created.Id);

        var saved = harness.ReadStatusFile(created.Id).State;
        Assert.NotEqual(ProjectState.Stopped, saved);
        Assert.Equal((await harness.Projects.GetStatusAsync(created.Id)).State, saved);
    }

    [Fact]
    public async Task CreateWithoutItsExecutable_IsError_WithWhy()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"godmode-no-claude-{Guid.NewGuid():N}", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        await using var harness = new LifecycleHarness(Waiting(),
            settings: new Dictionary<string, string?> { [ClaudeProcessManager.ExecutableSetting] = missing });
        var projectId = $"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p1";

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync());

        var status = await harness.Projects.GetStatusAsync(projectId);
        Assert.Equal(ProjectState.Error, status.State);
        Assert.Contains("claude", status.LastError);
        Assert.Equal(ProjectState.Error, harness.ReadStatusFile(projectId).State);
        Assert.Equal(status.LastError, harness.ReadStatusFile(projectId).LastError);
    }

    /// <summary>
    /// A Resume with nothing to say: claude is started on the session and waits for input, writing
    /// nothing until it has some, so the project is Idle, not Running, until the user writes.
    /// </summary>
    [Fact]
    public async Task BareResume_OnAStoppedProject_IsIdle_UntilTheUserWrites()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.StopProjectAsync(created.Id);
        harness.UseScript(new FakeScript().AwaitStdin().EmitInit().AwaitStdin());

        await harness.Projects.ResumeProjectAsync(created.Id);

        var resumed = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        await Task.Delay(300);
        Assert.True(LifecycleHarness.IsProcessAlive(resumed.Pid));
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Equal(ProjectState.Idle, harness.ReadStatusFile(created.Id).State);
        Assert.Equal(ProjectState.Idle, harness.Hub.StatusPushes(created.Id)[^1].State);

        await harness.Projects.SendInputAsync(created.Id, Reply);

        await harness.WaitForStateAsync(created.Id, ProjectState.Running);
        Assert.Single((await harness.WaitForStdinAsync(created.Id, index: 1)).Stdin);
    }
}
