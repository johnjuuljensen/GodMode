using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A project is identified by <c>{profile}/{root}/{name}</c>, not by its folder name alone: two
/// projects of one name in different roots are tracked, run and broadcast apart, a name that is
/// no folder of its own is refused, and folders from before the ID changed recover under it.
/// </summary>
public class ProjectIdentityTests
{
    private const string SecondRoot = "second";

    [Fact]
    public async Task SameNameInTwoRoots_StoppingOne_LeavesTheOtherRunning_WithItsOwnOutput()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().Turn("First from A.").AwaitStdin().EmitAssistant("Second from A.").EmitResult(),
            extraRoots: [(SecondRoot, LifecycleHarness.ProfileName)]);
        var a = await harness.CreateProjectAsync("same");
        await harness.WaitForStateAsync(a.Id, ProjectState.Idle); // A's fake has loaded its script
        harness.UseScript(new FakeScript().EmitInit().Turn("First from B.").AwaitStdin().EmitAssistant("Second from B.").EmitResult());
        var b = await harness.CreateProjectAsync("same", root: SecondRoot);
        var launchA = await harness.WaitForLaunchAsync(a.Id, l => l.Stdin.Count >= 1);
        var launchB = await harness.WaitForLaunchAsync(b.Id, l => l.Stdin.Count >= 1);
        await harness.WaitForStateAsync(b.Id, ProjectState.Idle);

        await harness.Projects.StopProjectAsync(a.Id);

        Assert.False(LifecycleHarness.IsProcessAlive(launchA.Pid), $"A's fake (pid {launchA.Pid}) is still running after A was stopped");
        Assert.True(LifecycleHarness.IsProcessAlive(launchB.Pid), $"B's fake (pid {launchB.Pid}) was stopped with A");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/same", a.Id);
        Assert.Equal($"{LifecycleHarness.ProfileName}/{SecondRoot}/same", b.Id);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(a.Id)).State);
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(b.Id)).State);

        await harness.Projects.SendInputAsync(b.Id, "Go on");
        await harness.WaitForStateAsync(b.Id, ProjectState.Idle);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(b.Id).Contains("Second from B.")), null,
            () => $"B's second turn is not in its output.jsonl.\n{harness.Describe(b.Id)}");
        Assert.DoesNotContain("from A", harness.ReadOutputFile(b.Id));
        Assert.DoesNotContain("from B", harness.ReadOutputFile(a.Id));
        var outputB = OutputPushes(harness, b.Id);
        Assert.Contains(outputB, json => json.Contains("Second from B."));
        Assert.DoesNotContain(outputB, json => json.Contains("from A"));
        Assert.DoesNotContain(OutputPushes(harness, a.Id), json => json.Contains("from B"));
        Assert.Equal(2, (await harness.Projects.ListProjectsAsync()).Length);
    }

    /// <summary>
    /// A name that is no folder of its own would put the project in its root, or in the root's
    /// parent, and a later delete would delete that recursively. It is refused before anything runs.
    /// </summary>
    [Theory]
    [InlineData("..", false)]
    [InlineData("..", true)]
    [InlineData(".", false)]
    [InlineData(".", true)]
    [InlineData("...", true)]
    [InlineData("/", false)]
    [InlineData("/..", true)]
    public async Task NameThatIsNoFolderOfItsOwn_IsRefused_AndNothingIsCreatedOrDeleted(string name, bool reuseExisting)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var before = Tree(harness.WorkDir);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(name,
            inputs: new Dictionary<string, object> { ["__reuseExisting"] = reuseExisting }));

        Assert.Equal(before, Tree(harness.WorkDir));
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>A root whose scripts create the folder is refused the same name before its scripts run.</summary>
    [Fact]
    public async Task NameThatIsNoFolderOfItsOwn_IsRefused_BeforeTheScriptsRun()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), "New-Item -ItemType Directory -Force $env:GODMODE_PROJECT_PATH");
        var before = Tree(harness.WorkDir);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(".."));

        Assert.Equal(before, Tree(harness.WorkDir));
    }

    /// <summary>
    /// A create script's <c>project_path</c> that is the root itself or above it is refused too: the
    /// project keeps its own folder, and deleting it deletes only that.
    /// </summary>
    [Fact]
    public async Task ScriptProjectPathAtOrAboveTheRoot_IsRefused_AndDeleteRemovesOnlyTheProjectsFolder()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"),
            "Set-Content -Path $env:GODMODE_RESULT_FILE -Value \"project_path=$(Join-Path $env:GODMODE_ROOT_PATH '..')\"");

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(ProjectState.Error, project.State);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.True(Directory.Exists(harness.RootPath), "the root was deleted with the project");
        Assert.True(File.Exists(Path.Combine(harness.RootPath, ".godmode-root", "config.json")), "the root's config was deleted with the project");
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "p1")), "the project's own folder is still there");
    }

    /// <summary>
    /// A folder written before the ID changed (status.json <c>Id</c> the bare folder name) recovers
    /// under the new ID, which is written back to status.json. Its settings and session are kept:
    /// they never held the ID. The old bare ID no longer finds it.
    /// </summary>
    [Fact]
    public async Task FolderFromBeforeTheIdChanged_IsRecoveredUnderTheNewId_WithItsSettingsAndSession()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().EmitAssistant("Resumed.").EmitResult());
        const string sessionId = "0f8fad5b-d9cb-469f-a165-70867728950e";
        WriteOldShapeProject(Path.Combine(harness.RootPath, "old-one"), "old-one", sessionId, skipPermissions: true);
        var id = $"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/old-one";

        await harness.Projects.RecoverProjectsAsync();

        var summary = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(id, summary.Id);
        Assert.Equal(LifecycleHarness.RootName, summary.RootName);
        Assert.Equal(LifecycleHarness.ProfileName, summary.ProfileName);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(id)).State);
        Assert.Equal(id, harness.ReadStatusFile(id).Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.GetStatusAsync("old-one"));

        await harness.Projects.ResumeProjectAsync(id);

        var launch = await harness.WaitForLaunchAsync(id, _ => true);
        Assert.Equal(sessionId, launch.ArgValue("--resume"));
        Assert.Contains("--dangerously-skip-permissions", launch.Argv);
        Assert.Equal(id, launch.Environment["GODMODE_PROJECT_ID"]);
        Assert.NotNull(harness.ProjectInfo(id));
        await harness.WaitForStateAsync(id, ProjectState.Idle);
        var output = harness.ReadOutputFile(id);
        Assert.Contains("Before the ID changed.", output);
        Assert.Contains("Resumed.", output);
    }

    /// <summary>A root whose name begins with another root's name keeps its own projects.</summary>
    [Fact]
    public async Task ProjectInARootNamedAfterAnother_IsRecoveredInItsOwnRoot()
    {
        const string root = LifecycleHarness.RootName + "2";
        const string profile = "other";
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(), extraRoots: [(root, profile)]);
        WriteOldShapeProject(Path.Combine(harness.RootsDir, root, "x"), "x", Guid.NewGuid().ToString(), skipPermissions: false);

        await harness.Projects.RecoverProjectsAsync();

        var summary = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal($"{profile}/{root}/x", summary.Id);
        Assert.Equal(root, summary.RootName);
        Assert.Equal(profile, summary.ProfileName);
    }

    /// <summary>A project folder as the server wrote it before project IDs carried the profile and root.</summary>
    private static void WriteOldShapeProject(string folder, string id, string sessionId, bool skipPermissions)
    {
        var godMode = Path.Combine(folder, ".godmode");
        Directory.CreateDirectory(godMode);
        var now = DateTime.UtcNow;
        var status = new ProjectStatus(id, "Old one", ProjectState.Running, now, now, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        File.WriteAllText(Path.Combine(godMode, "status.json"), JsonSerializer.Serialize(status, JsonDefaults.Options));
        new GodMode.ProjectFiles.ProjectSettings(skipPermissions, "Create").Save(folder);
        File.WriteAllText(Path.Combine(godMode, "session-id"), sessionId);
        File.WriteAllText(Path.Combine(godMode, "output.jsonl"),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "Before the ID changed." } } } }) + "\n");
    }

    private static string[] OutputPushes(LifecycleHarness harness, string projectId) =>
        harness.Hub.Pushes.Where(p => p.Method == nameof(IProjectHubClient.OutputReceived) && p.ProjectId == projectId)
            .Select(p => p.RawJson!).ToArray();

    /// <summary>Every directory and file under <paramref name="dir"/>, with each file's size.</summary>
    private static string[] Tree(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(path => File.Exists(path) ? $"{Path.GetRelativePath(dir, path)} {new FileInfo(path).Length}" : Path.GetRelativePath(dir, path) + "/")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
