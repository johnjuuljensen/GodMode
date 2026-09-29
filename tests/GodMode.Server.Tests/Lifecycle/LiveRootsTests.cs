using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using SessionState = GodMode.ProjectFiles.SessionState;
using ProjectSettings = GodMode.ProjectFiles.ProjectSettings;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Live roots: once the startup's recovery has run, a root added, edited or removed on the host or in
/// the config reaches connected clients as <see cref="IProjectHubClient.RootsChanged"/>, only when the
/// lists changed, from a poll (<see cref="ProjectManager.RootsPollSetting"/>) or a config reload. A
/// root that appears has its sessions recovered; a root that goes takes its sessions without a claude
/// out of the list, and keeps its lock and its running sessions until they stop. A tracked session is
/// its root's by folder, and takes the ID its root's folder gives it once it has no claude.
/// </summary>
public sealed class LiveRootsTests : IDisposable
{
    /// <summary>Folders outside the harness's scan folder: explicit roots, and roots made before they are moved in.</summary>
    private readonly string _elsewhere = ServerProcess.CreateWorkDir("live-roots");

    public void Dispose() => ServerProcess.DeleteWorkDir(_elsewhere);

    private string Elsewhere(params string[] parts) => Path.Combine([_elsewhere, .. parts]);

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>The poll at <paramref name="seconds"/> (0: off), with any more settings.</summary>
    private static Dictionary<string, string?> Poll(double seconds) =>
        new() { [ProjectManager.RootsPollSetting] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>A root folder at <paramref name="path"/>, whose config.json has <paramref name="config"/>.</summary>
    private static string WriteRoot(string path, object config)
    {
        var configDir = Path.Combine(path, ".godmode-root");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>Sets the description in the root's config.json, as a save of it by hand would.</summary>
    private static void EditDescription(LifecycleHarness harness, string description)
    {
        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["description"] = JsonSerializer.SerializeToElement(description);
        File.WriteAllText(config, JsonSerializer.Serialize(edited));
    }

    /// <summary>
    /// Moves a folder in or out of a root. On Windows a folder with a file open in it cannot be moved,
    /// and the server opens the root's config for as long as a read of it takes (a refresh, or the pull
    /// request check a session's stop starts): a move that lands on one is refused, and is tried again.
    /// </summary>
    private static async Task MoveAsync(string from, string to)
    {
        Exception? refused = null;
        await LifecycleHarness.WaitUntilAsync(() =>
        {
            try
            {
                Directory.Move(from, to);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused = ex;
                return Task.FromResult(false);
            }
        }, null, () => $"{from} could not be moved to {to}: {refused?.Message}");
    }

    /// <summary>
    /// Reads the roots a few times. Refreshes run one at a time, and each records its push before the
    /// next starts, so once these return every poll before them has pushed what it found.
    /// </summary>
    private static async Task ReadAgainAsync(LifecycleHarness harness)
    {
        for (var i = 0; i < 3; i++) await harness.Projects.ListProjectRootsAsync();
    }

    private static Task WaitForRootsPushAsync(LifecycleHarness harness, Func<HubPush, bool> condition, string what) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.RootsPushes.Any(condition)), null,
            () => $"no RootsChanged {what}; pushed: {string.Join(" | ", harness.Hub.RootsPushes.Select(Names))}\n{string.Join("\n", harness.Warnings)}");

    private static Task WaitForPushAsync(LifecycleHarness harness, string method, string projectId) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.Pushes.Any(p => p.Method == method && p.ProjectId == projectId)), null,
            () => $"no {method} for {projectId}; pushed: {string.Join(", ", harness.Hub.Pushes.Select(p => $"{p.Method} {p.ProjectId}"))}");

    private static string Names(HubPush push) => string.Join(",", push.Roots!.Select(root => $"{root.ProfileName}/{root.Name}"));

    private static bool Lists(HubPush push, string profile, string root) =>
        push.Roots!.Any(r => r.Name == root && r.ProfileName == profile);

    private static int IndexOf(LifecycleHarness harness, Func<HubPush, bool> condition) =>
        harness.Hub.Pushes.Select((push, index) => (push, index)).First(p => condition(p.push)).index;

    [Fact]
    public async Task RootFolderCreatedInAScanFolder_IsPushedWithinThePoll_AndListed()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.2));
        await harness.Projects.RecoverProjectsAsync();

        WriteRoot(Path.Combine(harness.RootsDir, "fresh"), new { profileName = "fresh-profile", description = "new here" });

        await WaitForRootsPushAsync(harness, push => Lists(push, "fresh-profile", "fresh"), "listing the new root");
        var push = harness.Hub.RootsPushes.First(p => Lists(p, "fresh-profile", "fresh"));
        Assert.True(Lists(push, LifecycleHarness.ProfileName, LifecycleHarness.RootName));
        Assert.Contains(push.Profiles!, profile => profile.Name == "fresh-profile");
        Assert.Equal("new here", Assert.Single(await harness.Projects.ListProjectRootsAsync(), root => root.Name == "fresh").Description);
    }

    /// <summary>With the poll off, a reload of the instance's config file is what reads the roots again.</summary>
    [Fact]
    public async Task ExplicitRootAddedToTheConfigFile_IsPushedOnTheReload()
    {
        var instanceFile = Elsewhere("instance.json");
        File.WriteAllText(instanceFile, "{}");
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0), configFiles: [instanceFile]);
        await harness.Projects.RecoverProjectsAsync();

        WriteRoot(Elsewhere("notes"), new { description = "an explicit root" });
        File.WriteAllText(instanceFile, JsonSerializer.Serialize(new
        {
            Roots = new { Explicit = new { notes = new { Path = Elsewhere("notes"), Profile = "Private" } } },
        }));
        harness.ReloadConfiguration();

        await WaitForRootsPushAsync(harness, push => Lists(push, "Private", "notes"), "listing the explicit root");
        Assert.Contains(await harness.Projects.ListProjectRootsAsync(), root => root.Name == "notes" && root.ProfileName == "Private");
    }

    /// <summary>Reads that find the roots as they were push nothing, however many; an edit to a root pushes once.</summary>
    [Fact]
    public async Task RootsReadAgainUnchanged_PushNothing_AndAnEditPushesOnce()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.1));
        await harness.Projects.RecoverProjectsAsync();

        await ReadAgainAsync(harness);
        Assert.Empty(harness.Hub.RootsPushes);

        EditDescription(harness, "edited");

        await WaitForRootsPushAsync(harness, push => push.Roots!.Any(root => root.Description == "edited"), "with the edited description");
        await ReadAgainAsync(harness);
        Assert.Single(harness.Hub.RootsPushes);
    }

    /// <summary>
    /// A root's config.json saved while a refresh reads the roots, once the refresh has read it, is
    /// pushed once, whole, by the next refresh: the root and the profile it describes (a profile
    /// without its own description takes its root's) as the save has them. A refresh that read the
    /// file twice pushed a root that did not match its profile, then pushed again.
    /// </summary>
    [Fact]
    public async Task ConfigSavedDuringARefresh_IsPushedOnce_WithTheRootAndItsProfileAgreeing()
    {
        EditAfterNextRead? reader = null;
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0), rootConfigReader: inner => reader = new EditAfterNextRead(inner));
        await harness.Projects.RecoverProjectsAsync();

        reader!.Arm(harness.RootPath, () => EditDescription(harness, "edited"));
        await harness.Projects.ListProjectRootsAsync();
        Assert.False(reader.Armed, "the refresh should have read the root's config, and the edit been saved");
        await ReadAgainAsync(harness);

        var push = Assert.Single(harness.Hub.RootsPushes);
        Assert.Equal("edited", Assert.Single(push.Roots!).Description);
        Assert.Equal("edited", Assert.Single(push.Profiles!).Description);
    }

    /// <summary>Refreshes from every trigger at once, the poll, config reloads and lists, push each edit once, in order.</summary>
    [Fact]
    public async Task RefreshesFromThePollReloadsAndListsAtOnce_PushEachEditOnce()
    {
        var instanceFile = Elsewhere("instance.json");
        File.WriteAllText(instanceFile, "{}");
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.01), configFiles: [instanceFile]);
        await harness.Projects.RecoverProjectsAsync();

        using var stop = new CancellationTokenSource();
        var reloads = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                harness.ReloadConfiguration();
                await Task.Delay(5);
            }
        });
        var lists = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) await harness.Projects.ListProjectRootsAsync();
        })).ToArray();
        const int edits = 5;
        for (var edit = 1; edit <= edits; edit++)
        {
            EditDescription(harness, $"edit {edit}");
            await WaitForRootsPushAsync(harness, push => push.Roots!.Any(root => root.Description == $"edit {edit}"), $"with edit {edit}");
        }
        stop.Cancel();
        await Task.WhenAll([reloads, .. lists]);
        await ReadAgainAsync(harness);

        Assert.Equal(Enumerable.Range(1, edits).Select(edit => $"edit {edit}"),
            harness.Hub.RootsPushes.Select(push => Assert.Single(push.Roots!).Description));
    }

    /// <summary>
    /// A root that appears with a session in it (moved in whole, or let go by another server) has the
    /// session recovered as the start recovers one, announced as ProjectCreated after the roots that list it.
    /// </summary>
    [Fact]
    public async Task RootThatAppearsWithASession_HasItRecovered()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.2));
        await harness.Projects.RecoverProjectsAsync();

        var outside = WriteRoot(Elsewhere("arrived"), new { profileName = LifecycleHarness.ProfileName });
        var state = LifecycleHarness.PlantSession(Path.Combine(outside, "work"), status: new ProjectStatus(
            "whatever/it/said", "Planted", ProjectState.Idle, DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0));
        new ProjectSettings(ActionName: "Create").Save(state);
        var arrived = Path.Combine(harness.RootsDir, "arrived");
        await MoveAsync(outside, arrived);

        var id = $"{LifecycleHarness.ProfileName}/arrived/{LifecycleHarness.PlantedSessionId}";
        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectCreated), id);

        Assert.True(IndexOf(harness, p => p.Method == nameof(IProjectHubClient.RootsChanged) && Lists(p, LifecycleHarness.ProfileName, "arrived"))
            < IndexOf(harness, p => p.Method == nameof(IProjectHubClient.ProjectCreated) && p.ProjectId == id));
        var listed = Assert.Single(await harness.Projects.ListProjectsAsync(), project => project.Id == id);
        Assert.Equal(("Planted", "arrived", ProjectState.Idle), (listed.Name, listed.RootName, listed.State));
        Assert.Equal(Path.Combine(arrived, "work"), harness.Tracked(id).ProjectPath);
    }

    /// <summary>
    /// A root that goes takes its sessions without a claude out of the list, leaving their files; one
    /// whose claude runs carries on, and the root stays held against other servers, until it stops. The
    /// root back brings its sessions back.
    /// </summary>
    [Fact]
    public async Task RemovedRoot_IdleSessionLeaves_RunningOneCarriesOnHoldingTheRoot_UntilItStops()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.2), extraRoots: [("other", "other")]);
        await harness.Projects.RecoverProjectsAsync();
        var running = await harness.CreateProjectAsync("running");
        await harness.WaitForStdinAsync(running.Id);
        var idle = await harness.CreateProjectAsync("idle");
        await harness.WaitForStdinAsync(idle.Id);
        await harness.Projects.StopProjectAsync(idle.Id);
        var idleState = harness.StatePath(idle.Id);

        var configDir = Path.Combine(harness.RootPath, ".godmode-root");
        await MoveAsync(configDir, configDir + ".away");

        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectDeleted), idle.Id);
        Assert.Contains(harness.Hub.RootsPushes, push => !Lists(push, LifecycleHarness.ProfileName, LifecycleHarness.RootName));
        Assert.True(Directory.Exists(idleState), "a session that leaves the list keeps its files");
        // Reads that agree the root is gone, after which a session without a claude has left
        await ReadAgainAsync(harness);
        Assert.True(harness.Lifecycle.IsRunning(harness.Tracked(running.Id)), "its claude runs on");
        Assert.DoesNotContain(harness.Hub.Pushes, p => p.Method == nameof(IProjectHubClient.ProjectDeleted) && p.ProjectId == running.Id);
        using (var taken = RootLock.TryAcquire(harness.RootPath, "another"))
            Assert.Null(taken);

        await harness.Projects.StopProjectAsync(running.Id);
        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectDeleted), running.Id);
        RootLock? free = null;
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult((free = RootLock.TryAcquire(harness.RootPath, "another")) != null), null,
            () => "the root was not let go once its last session had stopped");
        free!.Dispose();

        await MoveAsync(configDir + ".away", configDir);
        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectCreated), idle.Id);
        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectCreated), running.Id);
    }

    /// <summary>
    /// A reply that waits on a session's resume lock while a refresh lets the session go (its root
    /// removed) fails as for a session not found, and launches nothing: a claude started then would be
    /// one the server does not track, stop at shutdown or hold the root for.
    /// </summary>
    [Fact]
    public async Task ReplyThatWaitedWhileItsSessionLeftTheList_LaunchesNothing()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0), extraRoots: [("other", "other")]);
        await harness.Projects.RecoverProjectsAsync();
        var idle = await harness.CreateProjectAsync("idle");
        await harness.WaitForStdinAsync(idle.Id);
        await harness.Projects.StopProjectAsync(idle.Id);
        var configDir = Path.Combine(harness.RootPath, ".godmode-root");
        await MoveAsync(configDir, configDir + ".away");
        // The first read that finds the root gone only notes it
        await harness.Projects.ListProjectRootsAsync();

        // Held, so the forget of the second read, then the reply, queue behind it, in that order
        var resumeLock = harness.Tracked(idle.Id).Process.ResumeLock;
        await resumeLock.WaitAsync();
        var forget = harness.Projects.ListProjectRootsAsync();
        Assert.False(forget.IsCompleted, "the second read should wait on the session's resume lock to let it go");
        var reply = harness.Projects.ReplyAndResumeAsync(idle.Id, "carry on");
        resumeLock.Release();

        await forget;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reply);
        Assert.Null(((ProjectManager)harness.Projects).Tracked(idle.Id));
        Assert.Equal(1, harness.LaunchesAsked(idle.Id));
    }

    /// <summary>
    /// The overseer's note on #323: a root name that comes to name another folder (a new explicit root
    /// wins the clash) while a session of the old folder runs. The session is its root's by folder: its
    /// delete runs its own root's script, not the new root's.
    /// </summary>
    [Fact]
    public async Task RootNameThatComesToNameAnotherFolder_LeavesARunningSessionOnItsOwnRoot()
    {
        var instanceFile = Elsewhere("instance.json");
        File.WriteAllText(instanceFile, "{}");
        var marker = Elsewhere("deleted-by");
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0), configFiles: [instanceFile],
            rootConfig: new Dictionary<string, object> { ["delete"] = "delete.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"), $"Set-Content -Path '{marker}' -Value scanned");
        await harness.Projects.RecoverProjectsAsync();
        var running = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(running.Id);

        var explicitRoot = WriteRoot(Elsewhere("explicit"), new { profileName = LifecycleHarness.ProfileName, delete = "delete.ps1" });
        File.WriteAllText(Path.Combine(explicitRoot, ".godmode-root", "delete.ps1"), $"Set-Content -Path '{marker}' -Value explicit");
        File.WriteAllText(instanceFile, JsonSerializer.Serialize(new
        {
            Roots = new { Explicit = new Dictionary<string, object> { [LifecycleHarness.RootName] = new { Path = explicitRoot } } },
        }));
        harness.ReloadConfiguration();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Warnings.Any(line => line.Contains("clashes with the root"))), null,
            () => "the scanned root never lost the clash");
        // Twice, so a session without a claude would have left the list: this one runs
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProjectRootsAsync();

        await harness.Projects.DeleteProjectAsync(running.Id);

        Assert.Equal("scanned", File.ReadAllText(marker).Trim());
    }

    /// <summary>
    /// A root's <c>profileName</c> edited while the server runs shows up live. Its sessions' IDs name the
    /// profile: one without a claude is recovered under the new ID at once (the old one pushed as
    /// deleted, the new one as created, status.json rewritten), and one whose claude runs keeps its ID,
    /// which its MCP config carries, until claude exits, then does the same.
    /// </summary>
    [Fact]
    public async Task ProfileNameEdited_SessionsTakeTheNewProfilesId_ARunningOneOnceItStops()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: Poll(0.2));
        await harness.Projects.RecoverProjectsAsync();
        var running = await harness.CreateProjectAsync("running");
        await harness.WaitForStdinAsync(running.Id);
        var idle = await harness.CreateProjectAsync("idle");
        await harness.WaitForStdinAsync(idle.Id);
        await harness.Projects.StopProjectAsync(idle.Id);
        string Renamed(string id) => "renamed" + id[LifecycleHarness.ProfileName.Length..];

        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["profileName"] = JsonSerializer.SerializeToElement("renamed");
        File.WriteAllText(config, JsonSerializer.Serialize(edited));

        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectCreated), Renamed(idle.Id));
        Assert.Contains(harness.Hub.Pushes, p => p.Method == nameof(IProjectHubClient.ProjectDeleted) && p.ProjectId == idle.Id);
        Assert.Contains(harness.Hub.RootsPushes, push => Lists(push, "renamed", LifecycleHarness.RootName));
        Assert.Equal(Renamed(idle.Id), harness.ReadStatusFile(Renamed(idle.Id)).Id);
        Assert.True(harness.Lifecycle.IsRunning(harness.Tracked(running.Id)), "its claude runs on");

        await harness.Projects.StopProjectAsync(running.Id);
        await WaitForPushAsync(harness, nameof(IProjectHubClient.ProjectCreated), Renamed(running.Id));
        var listed = await harness.Projects.ListProjectsAsync();
        Assert.Equal([Renamed(idle.Id), Renamed(running.Id)], listed.Select(p => p.Id).Order());
        Assert.All(listed, project => Assert.Equal("renamed", project.ProfileName));
    }
}

/// <summary>A reader of root configs that saves an edit once its next read of a root has returned, in the middle of whatever read it.</summary>
internal sealed class EditAfterNextRead(IRootConfigReader inner) : IRootConfigReader
{
    private Pending? _armed;

    private sealed record Pending(string RootPath, Action Edit);

    public bool Armed => Volatile.Read(ref _armed) != null;

    public void Arm(string rootPath, Action edit) => _armed = new Pending(Path.GetFullPath(rootPath), edit);

    public RootConfig ReadConfig(string rootPath) => After(rootPath, inner.ReadConfig(rootPath));

    public RootConfig ReadConfigStrict(string rootPath) => After(rootPath, inner.ReadConfigStrict(rootPath));

    private RootConfig After(string rootPath, RootConfig read)
    {
        if (_armed is { } armed && string.Equals(Path.GetFullPath(rootPath), armed.RootPath, StringComparison.OrdinalIgnoreCase)
            && Interlocked.CompareExchange(ref _armed, null, armed) == armed)
            armed.Edit();
        return read;
    }
}
