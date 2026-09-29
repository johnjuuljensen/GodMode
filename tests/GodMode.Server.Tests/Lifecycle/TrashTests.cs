using System.Globalization;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using SessionState = GodMode.ProjectFiles.SessionState;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session that shares its working folder is deleted into the folder's trash: its state moves to
/// <c>.godmode/trash/{id}/</c>, and <see cref="IProjectHub.RestoreProject"/> brings it back under the
/// same ID until the trash is purged, at the start and on its schedule. A worktree's delete removes
/// its folder, which nothing brings back. A restore that would give the session another ID (its root
/// removed, or moved to another profile) fails, and leaves it in the trash.
/// </summary>
public class TrashTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private static readonly Dictionary<string, object> Shared = new() { ["sharedFolder"] = true };

    private static string IdOf(string projectId) => projectId.Split('/')[^1];

    private static string TrashedPath(LifecycleHarness harness, string projectId) =>
        SessionState.TrashedPathOf(harness.ProjectPath(projectId), IdOf(projectId));

    private static async Task<(ProjectStatus First, ProjectStatus Second)> TwoInOneFolderAsync(LifecycleHarness harness)
    {
        var first = await harness.CreateProjectAsync("assistant", "first");
        var second = await harness.CreateProjectAsync("assistant", "second");
        await harness.WaitForStdinAsync(first.Id);
        await harness.WaitForStdinAsync(second.Id);
        return (first, second);
    }

    /// <summary>
    /// The delete of a shared session moves its state, whole, into the folder's trash, and says so; the
    /// restore puts it back under the same ID, Stopped, with its output, pushes it as created, and a
    /// restart recovers it as any other.
    /// </summary>
    [Fact]
    public async Task SharedDelete_TrashesTheState_AndRestoreBringsItBackUnderTheSameId()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var (first, second) = await TwoInOneFolderAsync(harness);
        var listed = (await harness.Projects.ListProjectsAsync()).Single(p => p.Id == first.Id);
        Assert.True(listed.SharedFolder, "the list does not say the session shares its folder");
        Assert.Equal("Create", listed.ActionName);

        var deleted = await harness.Projects.DeleteProjectAsync(first.Id);

        Assert.True(deleted.Trashed, "a shared session's delete did not say it was trashed");
        Assert.False(Directory.Exists(harness.StatePath(first.Id)), "the state is still in sessions/");
        var trashed = TrashedPath(harness, first.Id);
        Assert.True(File.Exists(Path.Combine(trashed, "output.jsonl")), "the state was not moved to the trash");
        Assert.True(File.Exists(Path.Combine(trashed, SessionState.TrashedAtFileName)));
        var output = File.ReadAllText(Path.Combine(trashed, "output.jsonl"));
        Assert.NotEmpty(output);
        Assert.Equal(second.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);

        var restored = await harness.Projects.RestoreProjectAsync(first.Id);

        Assert.Equal(first.Id, restored.Id);
        Assert.Equal(ProjectState.Stopped, restored.State);
        Assert.True(restored.SharedFolder);
        Assert.False(Directory.Exists(trashed), "the trash still has it");
        Assert.False(File.Exists(Path.Combine(harness.StatePath(first.Id), SessionState.TrashedAtFileName)));
        Assert.Equal(output, harness.ReadOutputFile(first.Id));
        Assert.Contains(harness.Hub.Pushes, p => p.Method == nameof(IProjectHubClient.ProjectCreated) && p.ProjectId == first.Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            (await harness.Projects.ListProjectsAsync()).Select(p => p.Id).Order(StringComparer.Ordinal));

        await harness.RestartAsync(resume: false);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(first.Id)).State);
    }

    /// <summary>A restored session can be deleted, and restored, again: nothing of the first trip is left in the way.</summary>
    [Fact]
    public async Task RestoredSession_IsDeletedAndRestoredAgain()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var (first, _) = await TwoInOneFolderAsync(harness);

        await harness.Projects.DeleteProjectAsync(first.Id);
        await harness.Projects.RestoreProjectAsync(first.Id);
        Assert.True((await harness.Projects.DeleteProjectAsync(first.Id)).Trashed);
        Assert.Equal(first.Id, (await harness.Projects.RestoreProjectAsync(first.Id)).Id);
    }

    /// <summary>A session in the list, or one never deleted, has nothing to restore.</summary>
    [Fact]
    public async Task Restore_OfASessionInTheList_OrNeverTrashed_Fails()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var (first, _) = await TwoInOneFolderAsync(harness);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.RestoreProjectAsync(first.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.Projects.RestoreProjectAsync($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/260101-create-never-abcd"));
    }

    /// <summary>A worktree's delete removes its folder, says nothing was trashed, and there is nothing to restore.</summary>
    [Fact]
    public async Task WorktreeDelete_RemovesTheFolder_AndNothingIsRestored()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync("worktree");
        await harness.WaitForStdinAsync(created.Id);
        Assert.False((await harness.Projects.ListProjectsAsync()).Single().SharedFolder);
        var folder = harness.ProjectPath(created.Id);

        var deleted = await harness.Projects.DeleteProjectAsync(created.Id);

        Assert.False(deleted.Trashed);
        Assert.False(Directory.Exists(folder), "the worktree's folder is still there");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RestoreProjectAsync(created.Id));
    }

    /// <summary>
    /// The root's profile renamed after the delete: the ID it had names no root now, so the restore fails
    /// rather than bring it back under the new profile's ID, and the session stays in the trash.
    /// </summary>
    [Fact]
    public async Task Restore_AfterTheRootMovedProfile_Fails_AndLeavesItInTheTrash()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared, settings: NoPoll);
        await harness.Projects.RecoverProjectsAsync();
        var (first, _) = await TwoInOneFolderAsync(harness);
        await harness.Projects.DeleteProjectAsync(first.Id);
        var trashed = TrashedPath(harness, first.Id);
        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["profileName"] = JsonSerializer.SerializeToElement("renamed");
        File.WriteAllText(config, JsonSerializer.Serialize(edited));

        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RestoreProjectAsync(first.Id));

        Assert.Contains("would not keep its ID", refused.Message);
        Assert.True(Directory.Exists(trashed), "a refused restore took it out of the trash");
        Assert.DoesNotContain(await harness.Projects.ListProjectsAsync(), p => p.Id.EndsWith(IdOf(first.Id)));
    }

    /// <summary>The root removed after the delete (its config folder gone): the restore fails, and the trash keeps it.</summary>
    [Fact]
    public async Task Restore_AfterTheRootWasRemoved_Fails_AndLeavesItInTheTrash()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared, settings: NoPoll);
        await harness.Projects.RecoverProjectsAsync();
        var (first, second) = await TwoInOneFolderAsync(harness);
        await harness.Projects.DeleteProjectAsync(first.Id);
        await harness.Projects.StopProjectAsync(second.Id);
        var trashed = TrashedPath(harness, first.Id);
        var configDir = Path.Combine(harness.RootPath, ".godmode-root");
        Directory.Move(configDir, configDir + ".away");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RestoreProjectAsync(first.Id));

        Assert.True(Directory.Exists(trashed), "a refused restore took it out of the trash");
    }

    /// <summary>
    /// A folder that a session owning it is in now does not take the trashed session back: its delete
    /// would remove the restored one's state with the folder. Nothing moves.
    /// </summary>
    [Fact]
    public async Task Restore_IntoAFolderAnUnsharedSessionIsInNow_IsRefused()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var (first, second) = await TwoInOneFolderAsync(harness);
        await harness.Projects.DeleteProjectAsync(first.Id);
        // A session on disk that does not share (it has no settings.json saying it does)
        LifecycleHarness.PlantSession(harness.ProjectPath(first.Id), status: new ProjectStatus(LifecycleHarness.PlantedId(), "owner",
            ProjectState.Stopped, DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0));

        await Assert.ThrowsAsync<ProjectInUseException>(() => harness.Projects.RestoreProjectAsync(first.Id));

        Assert.True(Directory.Exists(TrashedPath(harness, first.Id)));
        Assert.NotNull(await harness.Projects.GetStatusAsync(second.Id));
    }

    /// <summary>
    /// The start purges the trash of what is older than the retention (a day), before anything can be
    /// restored, and keeps what is newer. A trash without its trashed-at file is dated by its folder.
    /// </summary>
    [Fact]
    public async Task Start_PurgesTrashOlderThanTheRetention_AndKeepsNewer()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: NoPoll);
        var folder = Path.Combine(harness.RootPath, "workspace");
        var old = Trash(folder, "260101-chat-old-aaaa", DateTime.UtcNow.AddDays(-2));
        var fresh = Trash(folder, "260101-chat-fresh-bbbb", DateTime.UtcNow.AddHours(-1));
        var undated = Trash(folder, "260101-chat-undated-cccc", DateTime.UtcNow.AddDays(-3));
        File.Delete(Path.Combine(undated, SessionState.TrashedAtFileName));
        Directory.SetLastWriteTimeUtc(undated, DateTime.UtcNow.AddDays(-3));

        await harness.Projects.RecoverProjectsAsync();

        Assert.False(Directory.Exists(old), "the start left trash older than a day");
        Assert.False(Directory.Exists(undated), "the start left an undated trash older than a day");
        Assert.True(Directory.Exists(fresh), "the start purged trash newer than a day");
    }

    /// <summary>The purge runs on its schedule too: a trashed session older than the retention goes, and cannot be restored.</summary>
    [Fact]
    public async Task Purge_OnItsSchedule_RemovesTrashThatAged()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared, settings: new Dictionary<string, string?>
        {
            [ProjectManager.RootsPollSetting] = "0",
            [ProjectManager.TrashRetentionSetting] = "0.5",
            [ProjectManager.TrashPurgeSetting] = "0.2",
        });
        await harness.Projects.RecoverProjectsAsync();
        var (first, _) = await TwoInOneFolderAsync(harness);
        await harness.Projects.DeleteProjectAsync(first.Id);
        var trashed = TrashedPath(harness, first.Id);

        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(!Directory.Exists(trashed)), null, () => "the schedule never purged the trash");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.RestoreProjectAsync(first.Id));
    }

    /// <summary>
    /// A shared create whose script fails, in a folder it made, takes that folder with its delete: nothing
    /// of it is left behind. One whose folder another session is in leaves the folder.
    /// </summary>
    [Fact]
    public async Task FailedSharedCreate_ItsDeleteRemovesTheFolderItMade_NotOneOthersUse()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object>(Shared) { ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"),
            "if ($env:GODMODE_INPUT_FAIL -eq 'yes') { exit 3 }");
        var fail = new Dictionary<string, object> { ["fail"] = "yes" };

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync("alone", inputs: fail));
        var alone = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(ProjectState.Error, alone.State);
        var aloneFolder = Path.Combine(harness.RootPath, "alone");
        Assert.True(Directory.Exists(aloneFolder));

        Assert.False((await harness.Projects.DeleteProjectAsync(alone.Id)).Trashed);
        Assert.False(Directory.Exists(aloneFolder), "the failed create's own folder was left behind");

        var used = await harness.CreateProjectAsync("used");
        await harness.WaitForStdinAsync(used.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync("used", inputs: fail));
        var failed = (await harness.Projects.ListProjectsAsync()).Single(p => p.State == ProjectState.Error);
        await harness.Projects.DeleteProjectAsync(failed.Id);
        Assert.True(Directory.Exists(harness.StatePath(used.Id)), "the folder another session is in went with a failed create");
    }

    /// <summary>
    /// A session whose root moved to another profile while its claude ran is still its old profile's:
    /// its delete script gets that profile's environment, which the server's config still has, and the
    /// root's delete script runs.
    /// </summary>
    [Fact]
    public async Task Delete_OfARunningSessionWhoseRootMovedProfile_RunsTheScriptWithItsProfilesEnvironment()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: NoPoll,
            rootConfig: new Dictionary<string, object> { ["delete"] = "delete.ps1" },
            profileEnvironment: new Dictionary<string, string> { ["TRASH_TEST_PROFILE"] = "the old profile's" });
        var told = Path.Combine(harness.WorkDir, "told.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"),
            $"Set-Content -Path '{told}' -Value \"ran with $env:TRASH_TEST_PROFILE\"");
        await harness.Projects.RecoverProjectsAsync();
        var running = await harness.CreateProjectAsync("running");
        await harness.WaitForStdinAsync(running.Id);
        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["profileName"] = JsonSerializer.SerializeToElement("renamed");
        File.WriteAllText(config, JsonSerializer.Serialize(edited));
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProjectRootsAsync();

        await harness.Projects.DeleteProjectAsync(running.Id);

        Assert.Equal("ran with the old profile's", File.ReadAllText(told).Trim());
    }

    private static readonly Dictionary<string, string?> NoPoll = new() { [ProjectManager.RootsPollSetting] = "0" };

    /// <summary>A trashed session's state in <paramref name="folder"/>, as a delete leaves it, trashed at <paramref name="at"/>.</summary>
    private static string Trash(string folder, string sessionId, DateTime at)
    {
        LifecycleHarness.PlantSession(folder, sessionId, new ProjectStatus($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/{sessionId}",
            sessionId, ProjectState.Stopped, at, at, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0));
        Assert.True(SessionState.Trash(folder, sessionId, at));
        var trashed = SessionState.TrashedPathOf(folder, sessionId);
        Assert.Equal(at.ToString("O", CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(trashed, SessionState.TrashedAtFileName)));
        return trashed;
    }
}
