using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session launches as the action the server recorded at its create or adopt (<see cref="FleetGrantFile"/>, in the
/// root's logs), never as the one its own settings.json names: that file is in its working folder, which it can write,
/// and another action's environment, permission mode, claudeArgs and model are not its to take (issue #399). A forget,
/// or a shared session's delete, sets the record aside in the root's logs, so a restore launches it as it was created.
/// </summary>
public class LaunchActionRecordTests
{
    private const string Work = "work";
    private const string Secret = "secret";
    private const string Overseer = "overseer";
    private const string Token = "TRACKER_TOKEN";

    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    private static readonly Dictionary<string, object> Shared = new() { ["sharedFolder"] = true };

    private static LifecycleHarness Harness(Dictionary<string, object>? rootConfig = null, Dictionary<string, string?>? settings = null)
    {
        var harness = new LifecycleHarness(Working(), rootConfig: rootConfig, settings: settings);
        WriteAction(harness, Work, "{}");
        WriteAction(harness, Secret, $$"""{ "environment": { "{{Token}}": "the-secret" }, "permissionMode": "acceptEdits", "model": "secret-model" }""");
        WriteAction(harness, Overseer, """{ "fleetTools": true }""");
        return harness;
    }

    private static void WriteAction(LifecycleHarness harness, string action, string json) =>
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", $"config.{action}.json"), json);

    private static async Task<string> CreateAsync(LifecycleHarness harness, string action, string name)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["prompt"] = JsonSerializer.SerializeToElement("Start"),
        };
        var status = (await harness.Projects.CreateProjectAsync(new CreateProjectRequest(
            LifecycleHarness.ProfileName, LifecycleHarness.RootName, inputs, action))).Project!;
        await harness.WaitForStdinAsync(status.Id);
        return status.Id;
    }

    private static string SessionIdOf(string projectId) => projectId.Split('/')[^1];

    /// <summary>Renames the session's action in its settings.json, at <paramref name="statePath"/>, as the session may.</summary>
    private static void RenameAction(string statePath, string action)
    {
        var settingsPath = Path.Combine(statePath, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["actionName"] = action;
        File.WriteAllText(settingsPath, settings.ToJsonString());
    }

    private static void AssertLaunchedAsWork(FakeLaunch launch)
    {
        Assert.False(launch.Environment.ContainsKey(Token), "the session launched with another action's environment");
        Assert.Null(launch.ArgValue("--permission-mode"));
        Assert.Null(launch.ArgValue("--model"));
    }

    private static void AssertLaunchedAsSecret(FakeLaunch launch)
    {
        Assert.Equal("the-secret", launch.Environment.GetValueOrDefault(Token));
        Assert.Equal("acceptEdits", launch.ArgValue("--permission-mode"));
        Assert.Equal("secret-model", launch.ArgValue("--model"));
    }

    private static async Task<FakeLaunch> ResumeAsync(LifecycleHarness harness, string projectId, int index)
    {
        await harness.Projects.ReplyAndResumeAsync(projectId, "Go on");
        return await harness.WaitForStdinAsync(projectId, index: index);
    }

    [Fact]
    public async Task ASessionThatRenamesItsActionInItsSettings_LaunchesAfterARestart_WithItsOwnActionsEnvironmentAndPermissionMode()
    {
        await using var harness = Harness();
        var worker = await CreateAsync(harness, Work, "worker");
        AssertLaunchedAsWork(harness.Launches(worker)[0]);

        RenameAction(harness.StatePath(worker), Secret);
        await harness.RestartAsync();

        AssertLaunchedAsWork(await harness.WaitForStdinAsync(worker, index: 1));
    }

    [Fact]
    public async Task ASessionThatRenamesItsAction_ResumesAsItsOwnAction()
    {
        await using var harness = Harness();
        var worker = await CreateAsync(harness, Work, "worker");

        RenameAction(harness.StatePath(worker), Secret);
        await harness.Projects.StopProjectAsync(worker);
        await harness.WaitForStateAsync(worker, ProjectState.Stopped);

        AssertLaunchedAsWork(await ResumeAsync(harness, worker, 1));
    }

    [Fact]
    public async Task AnAdoptedSessionThatRenamesItsAction_LaunchesAfterARestart_AsTheActionItWasAdoptedWith()
    {
        await using var harness = Harness();
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "existing"));
        await harness.Projects.RecoverProjectsAsync();
        var adopted = (await harness.Projects.AdoptFolderAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, "existing", Work, null)).Id;

        RenameAction(harness.StatePath(adopted), Secret);
        await harness.RestartAsync();

        AssertLaunchedAsWork(await ResumeAsync(harness, adopted, 0));
    }

    /// <summary>A session made before records were kept launches as its settings.json says, as it always has.</summary>
    [Fact]
    public async Task ASessionWithoutARecord_LaunchesAsItsSettingsSay()
    {
        await using var harness = Harness();
        var legacy = await CreateAsync(harness, Secret, "legacy");
        File.Delete(FleetGrantFile.PathFor(harness.RootPath, SessionIdOf(legacy)));

        await harness.RestartAsync();

        AssertLaunchedAsSecret(await harness.WaitForStdinAsync(legacy, index: 1));
    }

    /// <summary>A record of another folder is not the session's (<see cref="FleetGrantRecordTests.ARecordOfAnotherFolder_GrantsNothing"/>): it launches as its settings say.</summary>
    [Fact]
    public async Task ARecordOfAnotherFolder_IsNotTheSessions_ForItsLaunch()
    {
        await using var harness = Harness();
        var session = await CreateAsync(harness, Secret, "session");
        var sessionId = SessionIdOf(session);
        FleetGrantFile.Write(harness.RootPath, sessionId, FleetGrantFile.Read(harness.RootPath, sessionId)! with { Action = Work, Folder = "elsewhere" });

        await harness.Projects.StopProjectAsync(session);
        await harness.WaitForStateAsync(session, ProjectState.Stopped);

        AssertLaunchedAsSecret(await ResumeAsync(harness, session, 1));
    }

    [Fact]
    public async Task AForgottenSession_ThatRenamedItsAction_IsRestoredAsItsOwnAction_WithNoFleetTools()
    {
        await using var harness = Harness();
        var worker = await CreateAsync(harness, Work, "worker");
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        Assert.True(harness.Projects.HasFleetTools(overseer));

        RenameAction(harness.StatePath(worker), Secret);
        await harness.Projects.ForgetProjectAsync(worker);
        await harness.Projects.ForgetProjectAsync(overseer);
        Assert.False(File.Exists(FleetGrantFile.PathFor(harness.RootPath, SessionIdOf(worker))));

        await harness.Projects.RestoreProjectAsync(worker);
        await harness.Projects.RestoreProjectAsync(overseer);

        AssertLaunchedAsWork(await ResumeAsync(harness, worker, 1));
        // Restored, it is its own action again, and has no fleet tools: a restore grants nothing (#391)
        Assert.Equal(Overseer, FleetGrantFile.Read(harness.RootPath, SessionIdOf(overseer))!.Action);
        Assert.False(harness.Projects.HasFleetTools(overseer));
    }

    [Fact]
    public async Task ASharedSessionsDelete_ThatRenamedItsAction_IsRestoredAsItsOwnAction()
    {
        await using var harness = Harness(Shared);
        var worker = await CreateAsync(harness, Work, "worker");

        RenameAction(harness.StatePath(worker), Secret);
        Assert.True((await harness.Projects.DeleteProjectAsync(worker)).Trashed);
        await harness.Projects.RestoreProjectAsync(worker);

        AssertLaunchedAsWork(await ResumeAsync(harness, worker, 1));
    }

    /// <summary>One forgotten before the record was set aside (or whose set-aside record is gone) comes back as its settings say.</summary>
    [Fact]
    public async Task ARestore_WithNoSetAsideRecord_LaunchesAsItsSettingsSay()
    {
        await using var harness = Harness();
        var legacy = await CreateAsync(harness, Secret, "legacy");
        await harness.Projects.ForgetProjectAsync(legacy);
        File.Delete(FleetGrantFile.SetAsidePathFor(harness.RootPath, SessionIdOf(legacy)));

        await harness.Projects.RestoreProjectAsync(legacy);

        AssertLaunchedAsSecret(await ResumeAsync(harness, legacy, 1));
    }

    [Fact]
    public async Task AnUnsharedDelete_LeavesNoRecord_SetAsideOrNot()
    {
        await using var harness = Harness();
        var worker = await CreateAsync(harness, Work, "worker");

        Assert.False((await harness.Projects.DeleteProjectAsync(worker)).Trashed);

        Assert.False(File.Exists(FleetGrantFile.PathFor(harness.RootPath, SessionIdOf(worker))));
        Assert.False(File.Exists(FleetGrantFile.SetAsidePathFor(harness.RootPath, SessionIdOf(worker))));
    }

    [Fact]
    public async Task ThePurge_DeletesTheRecordSetAside()
    {
        await using var harness = Harness(Shared, new Dictionary<string, string?>
        {
            [ProjectManager.RootsPollSetting] = "0",
            [ProjectManager.TrashRetentionSetting] = "0.5",
            [ProjectManager.TrashPurgeSetting] = "0.2",
        });
        await harness.Projects.RecoverProjectsAsync();
        var worker = await CreateAsync(harness, Work, "worker");
        await harness.Projects.DeleteProjectAsync(worker);
        var setAside = FleetGrantFile.SetAsidePathFor(harness.RootPath, SessionIdOf(worker));
        Assert.True(File.Exists(setAside), "a shared session's delete did not set its record aside");

        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(!File.Exists(setAside)), null, () => "the purge left the record set aside");
    }
}
