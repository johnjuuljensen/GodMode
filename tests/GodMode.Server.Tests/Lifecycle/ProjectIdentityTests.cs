using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
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
    /// A folder the root keeps for itself is no project's: a delete of the project would delete the
    /// root's config and scripts, or every script log. Each is there already (<c>logs</c> after the
    /// first create, <c>.archived</c> left over from archiving), so a reuse would take it over. The
    /// create is refused before anything is written.
    /// </summary>
    [Theory]
    [InlineData(".godmode-root", false)]
    [InlineData(".godmode-root", true)]
    [InlineData("logs", false)]
    [InlineData("logs", true)]
    [InlineData(".archived", false)]
    [InlineData(".archived", true)]
    public async Task NameOfAFolderTheRootUses_IsRefused_AndNothingIsCreatedOrDeleted(string name, bool reuseExisting)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        WriteRootsOwnFolders(harness);
        var before = Tree(harness.WorkDir);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(name,
            inputs: new Dictionary<string, object> { ["__reuseExisting"] = reuseExisting }));

        Assert.Equal(before, Tree(harness.WorkDir));
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// A create script's <c>project_path</c> naming one of the root's own folders is refused too:
    /// the project keeps the folder it was given, and deleting it leaves the root's folders be.
    /// </summary>
    [Theory]
    [InlineData(".godmode-root")]
    [InlineData("logs")]
    [InlineData(".archived")]
    public async Task ScriptProjectPathOfAFolderTheRootUses_IsRefused_AndDeleteLeavesTheRootsFolders(string folder)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"),
            $"Set-Content -Path $env:GODMODE_RESULT_FILE -Value \"project_path=$(Join-Path $env:GODMODE_ROOT_PATH '{folder}')\"");
        var markers = WriteRootsOwnFolders(harness);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(ProjectState.Error, project.State);
        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p1", project.Id);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.All(markers, marker => Assert.True(File.Exists(marker), $"{marker} was deleted with the project"));
        Assert.True(File.Exists(Path.Combine(harness.RootPath, ".godmode-root", "config.json")), "the root's config was deleted with the project");
    }

    /// <summary>The root's folders are matched as Windows matches folder names: ignoring case, and trailing dots and spaces.</summary>
    [Theory]
    [InlineData("LOGS")]
    [InlineData("Logs.")]
    [InlineData("logs ")]
    [InlineData(".GodMode-Root")]
    [InlineData(".Archived..")]
    public void NameOfAFolderTheRootUses_IsRefused_InAnyCase(string name) =>
        Assert.Throws<ArgumentException>(() => GodMode.ProjectFiles.ProjectFolder.ValidateFolderName(name));

    /// <summary>
    /// A create script's <c>project_path</c> inside one of the root's own folders is the root's too:
    /// <c>{root}/.godmode-root/scripts</c> is a folder name of its own, and a delete of the project
    /// would delete the root's scripts. It is refused by its first folder under the root, not its last.
    /// </summary>
    [Theory]
    [InlineData(".godmode-root", "scripts")]
    [InlineData("logs", "x")]
    [InlineData("LOGS.", "x")]
    public async Task ScriptProjectPathInsideAFolderTheRootUses_IsRefused_AndDeleteLeavesIt(string reserved, string folder)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        WriteRootsOwnFolders(harness);
        var inside = Path.Combine(harness.RootPath, reserved.TrimEnd('.'), folder);
        var marker = WriteMarker(inside);
        WriteProjectPathScript(harness, Path.Combine(harness.RootPath, reserved, folder));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));
        Assert.Contains("uses for itself", refused.Message);
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(ProjectState.Error, project.State);
        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p1", project.Id);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.True(File.Exists(marker), $"{inside} was deleted with the project");
        Assert.True(File.Exists(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1")), "the root's scripts were deleted with the project");
    }

    /// <summary>
    /// A create script's <c>project_path</c> must be inside the script's own root: a sibling root's
    /// folder, or one outside the roots, is refused, and a delete of the project leaves it be.
    /// </summary>
    [Theory]
    [InlineData("sibling root")]
    [InlineData("outside the roots")]
    public async Task ScriptProjectPathOutsideItsRoot_IsRefused_AndDeleteLeavesIt(string where)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" },
            extraRoots: [(SecondRoot, LifecycleHarness.ProfileName)]);
        var elsewhere = where == "sibling root" ? Path.Combine(harness.RootsDir, SecondRoot, "x") : Path.Combine(harness.WorkDir, "outside", "x");
        var marker = WriteMarker(elsewhere);
        WriteProjectPathScript(harness, elsewhere);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));
        Assert.Contains("not inside its project root", refused.Message);
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/p1", project.Id);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.True(File.Exists(marker), $"{elsewhere} was deleted with the project");
    }

    /// <summary>
    /// A link inside the root to a folder elsewhere is that folder: a create script's
    /// <c>project_path</c> through it is refused (a junction on Windows, which needs no privilege; a
    /// symbolic link elsewhere), and a delete of the project leaves the folder be.
    /// </summary>
    [Fact]
    public async Task ScriptProjectPathThroughALinkOutOfItsRoot_IsRefused_AndDeleteLeavesIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        var outside = Path.Combine(harness.WorkDir, "outside");
        var marker = WriteMarker(outside);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), $$"""
            $link = Join-Path $env:GODMODE_ROOT_PATH 'escape'
            New-Item -ItemType ($IsWindows ? 'Junction' : 'SymbolicLink') -Path $link -Target '{{outside}}' | Out-Null
            Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$link"
            """);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));
        Assert.Contains("not inside its project root", refused.Message);
        Assert.NotNull(new DirectoryInfo(Path.Combine(harness.RootPath, "escape")).LinkTarget);
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.True(File.Exists(marker), $"{outside} was deleted through the link");
    }

    /// <summary>
    /// The delete itself refuses a folder that is not inside a known root, whatever the tracked
    /// project says: the project is forgotten, and its folder left on disk.
    /// </summary>
    [Fact]
    public async Task DeleteOfAProjectWhoseFolderIsOutsideTheRoots_IsRefused_AndTheFolderIsLeft()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var project = await harness.CreateProjectAsync("p1");
        var outside = Path.Combine(harness.WorkDir, "outside");
        var marker = WriteMarker(outside);
        harness.Tracked(project.Id).ProjectPath = outside;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.DeleteProjectAsync(project.Id));

        Assert.Contains("not inside a project root", refused.Message);
        Assert.True(File.Exists(marker), $"{outside} was deleted with the project");
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// A root's own folder with a <c>status.json</c> (written before such names were refused) is not
    /// recovered: it is never listed, resumed or deleted as a project.
    /// </summary>
    [Theory]
    [InlineData("logs")]
    [InlineData(".godmode-root")]
    [InlineData(".archived")]
    public async Task FolderTheRootUses_WithAStatusFile_IsNotRecovered(string folder)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        WriteOldShapeProject(Path.Combine(harness.RootPath, folder), folder, Guid.NewGuid().ToString(), skipPermissions: false);
        WriteOldShapeProject(Path.Combine(harness.RootPath, "real"), "real", Guid.NewGuid().ToString(), skipPermissions: false);

        await harness.Projects.RecoverProjectsAsync();

        var summary = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/real", summary.Id);
    }

    /// <summary>
    /// Windows drops a folder name's trailing dots, so <c>foo.</c> is the folder <c>foo</c>. The name
    /// is made the folder's, so the ID is the one the folder recovers under after a restart.
    /// </summary>
    [Fact]
    public async Task NameWithATrailingDot_IsTheFolderWithout_AndKeepsItsIdOverARestart()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());

        var project = await harness.CreateProjectAsync("foo.");

        Assert.Equal($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/foo", project.Id);
        Assert.Equal("foo", Path.GetFileName(harness.Tracked(project.Id).ProjectPath));
        await harness.RestartAsync(resume: false);
        Assert.Equal(project.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
    }

    /// <summary><c>foo.</c> is <c>foo</c>, so a reuse of it while <c>foo</c> is tracked is refused, not a second project in that folder.</summary>
    [Fact]
    public async Task ReuseOfANameWithATrailingDot_WhileTheFolderWithoutIsTracked_IsRefused()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var foo = await harness.CreateProjectAsync("foo");

        await Assert.ThrowsAsync<ProjectInUseException>(() => harness.CreateProjectAsync("foo.",
            inputs: new Dictionary<string, object> { ["__reuseExisting"] = true }));

        Assert.Equal(foo.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
    }

    /// <summary>
    /// A folder name Windows would change or not make (a trailing dot or space, a device name with
    /// or without an extension) is refused on every OS; names that only look like one are not.
    /// </summary>
    [Theory]
    [InlineData("foo.", false)]
    [InlineData("foo ", false)]
    [InlineData("foo. .", false)]
    [InlineData("CON", false)]
    [InlineData("con", false)]
    [InlineData("Nul", false)]
    [InlineData("nul.txt", false)]
    [InlineData("AUX .tar.gz", false)]
    [InlineData("COM1", false)]
    [InlineData("lpt9", false)]
    [InlineData("COM¹", false)]
    [InlineData("CONSOLE", true)]
    [InlineData("COM10", true)]
    [InlineData("con_", true)]
    [InlineData("nullable", true)]
    [InlineData("foo.bar", true)]
    [InlineData(".foo", true)]
    public void FolderNameWindowsWouldChange_IsRefused(string name, bool valid)
    {
        if (valid)
            GodMode.ProjectFiles.ProjectFolder.ValidateFolderName(name);
        else
            Assert.Throws<ArgumentException>(() => GodMode.ProjectFiles.ProjectFolder.ValidateFolderName(name));
    }

    /// <summary>A name's trailing dots are dropped from its folder, as Windows would; dots only is still no folder.</summary>
    [Theory]
    [InlineData("foo.", "foo")]
    [InlineData("foo...", "foo")]
    [InlineData("my fix.", "my_fix")]
    [InlineData("v1.2", "v1.2")]
    public void NameWithTrailingDots_IsTheFolderWithout(string name, string folder) =>
        Assert.Equal(folder, GodMode.ProjectFiles.ProjectManager.ConvertNameToPath(name));

    /// <summary>
    /// Scripts get the project's folder name as <c>GODMODE_PROJECT_FOLDER</c>. <c>GODMODE_PROJECT_ID</c>
    /// is gone: the project's ID is <c>{profile}/{root}/{folder}</c>, which no script is given.
    /// </summary>
    [Fact]
    public async Task Scripts_GetTheFolderName_AsGodModeProjectFolder()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        var seen = Path.Combine(harness.WorkDir, "seen.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"),
            $"Set-Content -Path '{seen}' -Value \"$env:GODMODE_PROJECT_FOLDER|$env:GODMODE_PROJECT_ID\"");

        await harness.CreateProjectAsync("my fix");

        Assert.Equal("my_fix|", File.ReadAllText(seen).Trim());
    }

    /// <summary>A create script that returns <paramref name="projectPath"/> as its <c>project_path</c>.</summary>
    private static void WriteProjectPathScript(LifecycleHarness harness, string projectPath) =>
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"),
            $"Set-Content -Path $env:GODMODE_RESULT_FILE -Value 'project_path={projectPath}'");

    /// <summary>Creates <paramref name="dir"/> with a file in it; returns the file.</summary>
    private static string WriteMarker(string dir)
    {
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "marker.txt");
        File.WriteAllText(marker, "not the project's");
        return marker;
    }

    /// <summary>The root's own folders as a root in use has them, each with a file in it; returns those files.</summary>
    private static string[] WriteRootsOwnFolders(LifecycleHarness harness)
    {
        var markers = new[] { ".godmode-root", "logs", ".archived" }
            .Select(folder => Path.Combine(harness.RootPath, folder, "marker.txt")).ToArray();
        foreach (var marker in markers)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "the root's own");
        }
        return markers;
    }

    /// <summary>
    /// A folder written before the ID changed (status.json <c>Id</c> the bare folder name) recovers
    /// under the new ID, which is written back to status.json. Its settings and session are kept:
    /// they never held the ID. The old bare ID no longer finds it. Its root allows skip-permissions, so
    /// the setting it kept is honoured.
    /// </summary>
    [Fact]
    public async Task FolderFromBeforeTheIdChanged_IsRecoveredUnderTheNewId_WithItsSettingsAndSession()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().EmitAssistant("Resumed.").EmitResult(),
            rootConfig: new Dictionary<string, object> { ["allowSkipPermissions"] = true });
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
        Assert.Equal(id, GodModeMcpEntry.Of(launch).ProjectId);
        Assert.NotNull(harness.ProjectInfo(id));
        // A bare resume is Idle before the resumed turn too: it is its output that says the turn ran
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(id).Contains("Resumed.")), null,
            () => $"the resumed turn is not in output.jsonl.\n{harness.Describe(id)}");
        await harness.WaitForStateAsync(id, ProjectState.Idle);
        Assert.Contains("Before the ID changed.", harness.ReadOutputFile(id));
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
