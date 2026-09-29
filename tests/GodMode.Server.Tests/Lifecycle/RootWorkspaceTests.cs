using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using ProjectSettings = GodMode.ProjectFiles.ProjectSettings;
using SessionState = GodMode.ProjectFiles.SessionState;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A root that is its own workspace: an existing repo with a <c>.godmode-root/</c>, whose shared
/// action's create script returns the root itself as its <c>project_path</c>. Its sessions run in the
/// root, with their state in the root's own <c>.godmode/sessions/</c>, and are recovered from there. A
/// delete, forced or not, whatever the delete script does, removes only the session's state (to the
/// trash): never the root, never its files. An action that does not share its folder may not name the root.
/// </summary>
public class RootWorkspaceTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>A shared action whose script makes no folder and names the root, as a single-folder root's does.</summary>
    private static Dictionary<string, object> InTheRoot(bool shared = true) => new()
    {
        ["sharedFolder"] = shared,
        ["scriptsCreateFolder"] = true,
        ["create"] = "create.ps1",
    };

    private const string CreateInTheRoot = """
        if ($env:GODMODE_INPUT_FAIL -eq 'yes') { exit 3 }
        Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$env:GODMODE_ROOT_PATH`nkind=chat"
        """;

    private static LifecycleHarness NewHarness(Dictionary<string, object>? rootConfig = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var harness = new LifecycleHarness(Waiting(), rootConfig: rootConfig ?? InTheRoot(), settings: settings);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), CreateInTheRoot);
        return harness;
    }

    /// <summary>The repo's own files, at its top level and below, as an existing repo has them; returns them.</summary>
    private static string[] WriteRepo(LifecycleHarness harness)
    {
        var files = new[] { "README.md", Path.Combine("src", "app.txt"), Path.Combine(".git", "HEAD"), Path.Combine("docs", "notes.md") }
            .Select(file => Path.Combine(harness.RootPath, file)).ToArray();
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "the repo's own");
        }
        return files;
    }

    /// <summary>Every file of the root but the server's (<c>.godmode/</c>, <c>logs/</c>) and the fake's records, with its content.</summary>
    private static string[] RootFiles(LifecycleHarness harness) =>
        Directory.EnumerateFiles(harness.RootPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(harness.RootPath, file))
            .Where(file => file.Split(Path.DirectorySeparatorChar)[0] is not (".godmode" or "logs") && !file.StartsWith("fake-claude-"))
            .Order(StringComparer.Ordinal)
            .Select(file => $"{file}: {File.ReadAllText(Path.Combine(harness.RootPath, file))}")
            .ToArray();

    private static string IdOf(string projectId) => projectId.Split('/')[^1];

    private static async Task<(ProjectStatus First, ProjectStatus Second)> TwoInTheRootAsync(LifecycleHarness harness)
    {
        var first = await harness.CreateProjectAsync("first");
        var second = await harness.CreateProjectAsync("second");
        await harness.WaitForStdinAsync(first.Id);
        await harness.WaitForStdinAsync(second.Id);
        return (first, second);
    }

    /// <summary>
    /// Two sessions run in the root itself, each with its state in the root's <c>.godmode/sessions/</c>
    /// (kept out of git), claude working in the root; nothing else is made in the root. A restart
    /// recovers both from there, as sharing the root, and carries on with them.
    /// </summary>
    [Fact]
    public async Task SharedAction_NamingTheRoot_CreatesRunsRestartsAndRecovers_TwoSessions()
    {
        await using var harness = NewHarness();
        WriteRepo(harness);
        var files = RootFiles(harness);

        var (first, second) = await TwoInTheRootAsync(harness);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal((harness.RootPath, harness.RootPath), (harness.ProjectPath(first.Id), harness.ProjectPath(second.Id)));
        Assert.Equal(new[] { IdOf(first.Id), IdOf(second.Id) }.Order(StringComparer.Ordinal), SessionState.List(harness.RootPath));
        Assert.True(first.SharedFolder && second.SharedFolder);
        // The fake writes its record in claude's working directory: the root
        Assert.Single(harness.Launches(first.Id));
        Assert.Single(harness.Launches(second.Id));
        Assert.Contains("*", File.ReadAllText(Path.Combine(harness.RootPath, ".godmode", ".gitignore")));
        Assert.Equal(files, RootFiles(harness));
        Assert.Equal(
            new[] { ".git", ".godmode", ".godmode-root", "docs", "logs", "src" },
            Directory.GetDirectories(harness.RootPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        await harness.RestartAsync();

        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            (await harness.Projects.ListProjectsAsync()).Select(p => p.Id).Order(StringComparer.Ordinal));
        foreach (var id in new[] { first.Id, second.Id })
        {
            Assert.Equal(harness.RootPath, harness.ProjectPath(id));
            Assert.True(harness.Tracked(id).SharedFolder, $"{id} was recovered as owning the root");
            await harness.WaitForLaunchAsync(id, launch => launch.Stdin.Count > 0, index: 1);
        }
        Assert.Equal(files, RootFiles(harness));
    }

    /// <summary>
    /// Deleting one session of the root takes its state to the trash, and only that: the other runs on,
    /// every file of the root stays, and the delete script is told the folder is shared. The last one
    /// leaves the root as it was; a restore brings a deleted one back into the root.
    /// </summary>
    [Fact]
    public async Task DeleteOfOne_LeavesTheOther_AndEveryFileInTheRoot()
    {
        var config = InTheRoot();
        config["delete"] = "delete.ps1";
        await using var harness = NewHarness(config);
        var told = Path.Combine(harness.WorkDir, "told.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"),
            $"Add-Content -Path '{told}' -Value \"$env:GODMODE_SESSION_ID $env:GODMODE_SHARED_FOLDER\"");
        WriteRepo(harness);
        var files = RootFiles(harness);
        var (first, second) = await TwoInTheRootAsync(harness);
        var secondLaunch = Assert.Single(harness.Launches(second.Id));

        Assert.True((await harness.Projects.DeleteProjectAsync(first.Id)).Trashed, "the root's session was not trashed");

        Assert.Equal($"{IdOf(first.Id)} true", File.ReadAllLines(told).Single());
        Assert.False(Directory.Exists(harness.StatePath(first.Id)));
        Assert.True(Directory.Exists(SessionState.TrashedPathOf(harness.RootPath, IdOf(first.Id))));
        Assert.True(Directory.Exists(harness.StatePath(second.Id)), "the other session's state went too");
        Assert.True(LifecycleHarness.IsProcessAlive(secondLaunch.Pid), "the other session's claude was stopped");
        Assert.Equal(second.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
        Assert.Equal(files, RootFiles(harness));

        var restored = await harness.Projects.RestoreProjectAsync(first.Id);
        Assert.Equal(harness.RootPath, harness.ProjectPath(restored.Id));

        await harness.Projects.DeleteProjectAsync(first.Id);
        await harness.Projects.DeleteProjectAsync(second.Id, force: true);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Empty(SessionState.List(harness.RootPath));
        Assert.Equal(files, RootFiles(harness));
        Assert.True(File.Exists(Path.Combine(harness.RootPath, ".godmode-root", "config.json")));
    }

    /// <summary>
    /// A forced delete, whose script fails, of a session in the root whose settings say it owns its
    /// folder still removes nothing but its state: a session in the root shares it, whatever it says.
    /// </summary>
    [Fact]
    public async Task ForcedDelete_OfASessionInTheRoot_RemovesNothingButItsState_EvenIfItClaimsTheFolder()
    {
        var config = InTheRoot();
        config["delete"] = "delete.ps1";
        await using var harness = NewHarness(config);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"),
            "if ($env:GODMODE_FORCE -ne 'true') { exit 1 }");
        WriteRepo(harness);
        var files = RootFiles(harness);
        var (first, second) = await TwoInTheRootAsync(harness);
        await harness.Projects.DeleteProjectAsync(second.Id, force: true);
        // As a tracked session whose settings say it owns its folder, and is the root's only one
        harness.Tracked(first.Id).SharedFolder = false;
        harness.Tracked(first.Id).MadeSharedFolder = true;

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Projects.DeleteProjectAsync(first.Id));
        Assert.True((await harness.Projects.DeleteProjectAsync(first.Id, force: true)).Trashed, "the forced delete did not trash the state");

        Assert.Equal(files, RootFiles(harness));
        Assert.False(Directory.Exists(harness.StatePath(first.Id)));
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// A session found in the root whose settings.json says it owns its folder is recovered as sharing
    /// the root, and its delete removes only its state.
    /// </summary>
    [Fact]
    public async Task SessionInTheRoot_WhoseSettingsSayItOwnsTheFolder_IsRecoveredShared_AndItsDeleteLeavesTheRoot()
    {
        await using var harness = NewHarness();
        WriteRepo(harness);
        var files = RootFiles(harness);
        var created = await harness.CreateProjectAsync("chat");
        await harness.WaitForStdinAsync(created.Id);
        var state = harness.StatePath(created.Id);
        (ProjectSettings.Load(state) with { SharedFolder = false }).Save(state);

        await harness.RestartAsync(resume: false);

        Assert.True(harness.Tracked(created.Id).SharedFolder, "a session in the root was recovered as owning it");
        await harness.Projects.DeleteProjectAsync(created.Id, force: true);
        Assert.Equal(files, RootFiles(harness));
        Assert.False(Directory.Exists(state));
    }

    /// <summary>
    /// An action that does not share its folder may not name the root, as before: the create is refused,
    /// saying why, and the delete of what it left removes only the folder the action gave it.
    /// </summary>
    [Fact]
    public async Task NonSharedAction_NamingTheRoot_IsRefused_AndItsDeleteLeavesTheRoot()
    {
        await using var harness = NewHarness(new Dictionary<string, object> { ["create"] = "create.ps1" });
        WriteRepo(harness);
        var files = RootFiles(harness);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("p1"));

        Assert.Contains("is the project root itself", refused.Message);
        Assert.Contains("sharedFolder", refused.Message);
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(ProjectState.Error, project.State);
        Assert.Equal(Path.Combine(harness.RootPath, "p1"), harness.ProjectPath(project.Id));
        Assert.Empty(SessionState.List(harness.RootPath));

        await harness.Projects.DeleteProjectAsync(project.Id, force: true);

        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "p1")), "the project's own folder is still there");
        Assert.Equal(files, RootFiles(harness));
    }

    /// <summary>
    /// A create into the root that fails leaves an Error project whose delete takes nothing of the
    /// root: with <c>scriptsCreateFolder</c> nothing was made, and without it only the folder the create
    /// made for its name goes, never one of the repo's that it found there.
    /// </summary>
    [Theory]
    [InlineData(true, "chat")]
    [InlineData(false, "chat")]
    [InlineData(false, "src")]
    public async Task FailedCreate_IntoTheRoot_ItsDeleteLeavesTheRoot(bool scriptsCreateFolder, string name)
    {
        var config = InTheRoot();
        config["scriptsCreateFolder"] = scriptsCreateFolder;
        await using var harness = NewHarness(config);
        WriteRepo(harness);
        var files = RootFiles(harness);
        var running = await harness.CreateProjectAsync("running");
        await harness.WaitForStdinAsync(running.Id);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync(name, inputs: new Dictionary<string, object> { ["fail"] = "yes" }));
        var failed = (await harness.Projects.ListProjectsAsync()).Single(p => p.State == ProjectState.Error);

        await harness.Projects.DeleteProjectAsync(failed.Id, force: true);

        Assert.Equal(files, RootFiles(harness));
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "chat")), "the folder the failed create made is still there");
        Assert.True(Directory.Exists(harness.StatePath(running.Id)), "the running session's state went with the failed create");
        Assert.Equal(running.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
    }

    /// <summary>The trash of the root's sessions is purged as any folder's, at the start, and the root's files stay.</summary>
    [Fact]
    public async Task TrashOfTheRootsSessions_IsPurged_AndTheRootStays()
    {
        await using var harness = NewHarness(settings: new Dictionary<string, string?>
        {
            [ProjectManager.RootsPollSetting] = "0",
            [ProjectManager.TrashRetentionSetting] = "0",
        });
        WriteRepo(harness);
        var files = RootFiles(harness);
        var created = await harness.CreateProjectAsync("chat");
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.DeleteProjectAsync(created.Id);
        var trashed = SessionState.TrashedPathOf(harness.RootPath, IdOf(created.Id));
        Assert.True(Directory.Exists(trashed));

        await harness.RestartAsync(resume: false);

        Assert.False(Directory.Exists(trashed), "the start left the root's trash");
        Assert.Equal(files, RootFiles(harness));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RestoreProjectAsync(created.Id));
    }

    /// <summary>
    /// The root's own <c>.godmode</c> is no folder for a session of its own: a create named after it is
    /// refused before anything is written, reused or not, so no delete can take the root's sessions.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSharedCreate_NamedAfterTheRootsGodModeFolder_IsRefused(bool reuseExisting)
    {
        await using var harness = NewHarness();
        var created = await harness.CreateProjectAsync("chat");
        await harness.WaitForStdinAsync(created.Id);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.work.json"), """{ "sharedFolder": false }""");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.Projects.CreateProjectAsync(new CreateProjectRequest(
            LifecycleHarness.ProfileName, LifecycleHarness.RootName, new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["name"] = System.Text.Json.JsonSerializer.SerializeToElement(".godmode"),
                ["prompt"] = System.Text.Json.JsonSerializer.SerializeToElement("hi"),
                ["__reuseExisting"] = System.Text.Json.JsonSerializer.SerializeToElement(reuseExisting),
            }, "work")));

        Assert.Contains("uses for itself", refused.Message);
        Assert.Equal(created.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
        Assert.True(Directory.Exists(harness.StatePath(created.Id)));
    }
}
