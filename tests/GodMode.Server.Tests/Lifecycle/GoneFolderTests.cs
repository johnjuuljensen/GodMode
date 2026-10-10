using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Hubs;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session whose folder is deleted outside GodMode (a worktree removed after its pull request
/// merged), its root still there (#501): once two reads of the roots in a row find its state folder
/// gone, a session without a claude leaves the list, pushed as ProjectDeleted, and its fleet record
/// and inbox go with it; one whose claude runs carries on until claude exits.
/// </summary>
public sealed class GoneFolderTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>The poll at <paramref name="seconds"/> (0: off).</summary>
    private static Dictionary<string, string?> Poll(double seconds) =>
        new() { [ProjectManager.RootsPollSetting] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Deletes a folder as a user or a script would, tried again while Windows has a file in it open for a moment.</summary>
    private static async Task DeleteAsync(string path)
    {
        Exception? refused = null;
        await LifecycleHarness.WaitUntilAsync(() =>
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused = ex;
                return Task.FromResult(false);
            }
        }, TestTimeouts.Wait, () => $"{path} could not be deleted: {refused?.Message}");
    }

    private static bool Deleted(LifecycleHarness harness, string projectId) =>
        harness.Hub.Pushes.Any(p => p.Method == nameof(IProjectHubClient.ProjectDeleted) && p.ProjectId == projectId);

    private static async Task<bool> ListedAsync(LifecycleHarness harness, string projectId) =>
        (await harness.Projects.ListProjectsAsync()).Any(project => project.Id == projectId);

    /// <summary>The issue's case: a stopped session's working folder deleted leaves the list within two polls, with what the server kept for it.</summary>
    [Fact]
    public async Task StoppedSession_WhoseFolderIsDeleted_LeavesTheListWithinTwoPolls()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.2));
        await harness.Projects.RecoverProjectsAsync();
        var project = await harness.CreateProjectAsync("gone");
        await harness.WaitForStdinAsync(project.Id);
        await harness.Projects.StopProjectAsync(project.Id);
        var sessionId = project.Id.Split('/')[^1];
        var fleetRecord = FleetGrantFile.PathFor(harness.RootPath, sessionId);
        Assert.True(File.Exists(fleetRecord), "a created session has a fleet record");
        var inbox = SessionInbox.PathFor(harness.RootPath, sessionId);
        File.WriteAllText(inbox, "");
        var log = Path.Combine(harness.RootPath, "logs", $"{sessionId}.log");
        var hadLog = File.Exists(log);

        await DeleteAsync(harness.ProjectPath(project.Id));

        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Deleted(harness, project.Id)), TestTimeouts.Wait,
            () => $"no ProjectDeleted for {project.Id}; pushed: {string.Join(", ", harness.Hub.Pushes.Select(p => $"{p.Method} {p.ProjectId}"))}");
        Assert.False(await ListedAsync(harness, project.Id));
        Assert.DoesNotContain(harness.Projects.GetAllAttention(), item => item.ProjectId == project.Id);
        Assert.False(File.Exists(fleetRecord), "its fleet record goes with it");
        Assert.False(File.Exists(inbox), "its inbox goes with it");
        Assert.Equal(hadLog, File.Exists(log));
    }

    /// <summary>
    /// One read that finds the state folder gone only notes it: a folder moved away for a moment and
    /// back by the next read keeps its session. Two reads in a row that find it gone let it go.
    /// </summary>
    [Fact]
    public async Task StateFolderGone_LeavesOnlyAtTheSecondReadInARow()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0));
        await harness.Projects.RecoverProjectsAsync();
        var project = await harness.CreateProjectAsync("moved");
        await harness.WaitForStdinAsync(project.Id);
        await harness.Projects.StopProjectAsync(project.Id);
        var state = harness.StatePath(project.Id);

        Directory.Move(state, state + ".away");
        await harness.Projects.ListProjectRootsAsync();
        Directory.Move(state + ".away", state);
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProjectRootsAsync();
        Assert.True(await ListedAsync(harness, project.Id), "a state folder back by the next read keeps its session");
        Assert.False(Deleted(harness, project.Id));

        await DeleteAsync(state);
        await harness.Projects.ListProjectRootsAsync();
        Assert.True(await ListedAsync(harness, project.Id), "the first read that finds it gone only notes it");
        Assert.False(Deleted(harness, project.Id));

        await harness.Projects.ListProjectRootsAsync();
        Assert.False(await ListedAsync(harness, project.Id), "the second read in a row lets it go");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Deleted(harness, project.Id)), TestTimeouts.Wait, () => $"no ProjectDeleted for {project.Id}");
        Assert.True(Directory.Exists(harness.ProjectPath(project.Id)), "its working folder, still there, is left as it is");
    }

    /// <summary>
    /// A session whose working folder is deleted while its claude runs carries on until claude exits,
    /// and leaves then. On Windows the delete cannot take the session's state: the server holds its
    /// claude's stderr (<c>errs.txt</c>) open in it, and claude has the folder as its working directory,
    /// so the session stays as a matter of course; it leaves once claude has exited and the rest is deleted.
    /// </summary>
    [Fact]
    public async Task RunningSession_WhoseFolderIsDeleted_StaysUntilClaudeExits()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0));
        await harness.Projects.RecoverProjectsAsync();
        var project = await harness.CreateProjectAsync("running");
        await harness.WaitForStdinAsync(project.Id);
        var folder = harness.ProjectPath(project.Id);

        if (OperatingSystem.IsWindows())
            Assert.ThrowsAny<IOException>(() => Directory.Delete(folder, recursive: true));
        else
            Directory.Delete(folder, recursive: true);
        for (var i = 0; i < 3; i++) await harness.Projects.ListProjectRootsAsync();
        Assert.True(await ListedAsync(harness, project.Id), "a session whose claude runs carries on");
        Assert.True(harness.Lifecycle.IsRunning(harness.Tracked(project.Id)), "its claude runs on");
        Assert.False(Deleted(harness, project.Id));

        await harness.Projects.StopProjectAsync(project.Id);
        await DeleteAsync(folder);
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProjectRootsAsync();
        Assert.False(await ListedAsync(harness, project.Id), "once claude has exited, it leaves");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Deleted(harness, project.Id)), TestTimeouts.Wait, () => $"no ProjectDeleted for {project.Id}");
    }
}
