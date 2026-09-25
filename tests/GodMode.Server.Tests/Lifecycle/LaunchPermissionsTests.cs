using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.ProjectFiles;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Skipping permissions is the root's to allow (<c>allowSkipPermissions</c>, off by default), and a
/// launch checks the root's config as it is then: the project's settings.json, which the session can
/// write, only asks. A root's <c>permissionMode</c> is kept with the project at create and passed as
/// <c>--permission-mode</c> to each of its launches; <c>bypassPermissions</c> is not one it can pick.
/// </summary>
public class LaunchPermissionsTests
{
    private const string Skip = "--dangerously-skip-permissions";
    private const string Mode = "--permission-mode";

    private static readonly Dictionary<string, object> AllowsSkip = new() { ["allowSkipPermissions"] = true };

    /// <summary>Waits for a prompt, reports it is working, and waits for the next one.</summary>
    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    private static Dictionary<string, object> AskingForSkip(object value) => new() { ["skipPermissions"] = value };

    private static string GodModeRoot(LifecycleHarness harness) => Path.Combine(harness.RootPath, ".godmode-root");

    /// <summary>Changes the root's config.json as the host would, keeping the harness's own keys.</summary>
    private static void EditRootConfig(LifecycleHarness harness, Action<JsonObject> edit)
    {
        var path = Path.Combine(GodModeRoot(harness), "config.json");
        WriteRootFile(path, Edited(File.ReadAllText(path), edit));
    }

    /// <summary>
    /// Writes a root's config file once a project is there. The server reads the root's config in the
    /// background too (a pull request check on each move to Idle or Stopped), and on Windows a write
    /// while it reads is refused: the write is tried again.
    /// </summary>
    private static void WriteRootFile(string path, string content)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.WriteAllText(path, content); return; }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private static string Edited(string json, Action<JsonObject> edit)
    {
        var config = JsonNode.Parse(json)!.AsObject();
        edit(config);
        return config.ToJsonString();
    }

    /// <summary>Changes the project's settings.json, as the session in it could.</summary>
    private static void EditSettings(LifecycleHarness harness, string projectId, Func<ProjectSettings, ProjectSettings> edit)
    {
        var folder = harness.ProjectPath(projectId);
        edit(ProjectSettings.Load(folder)).Save(folder);
    }

    private static async Task StopAsync(LifecycleHarness harness, string projectId)
    {
        await harness.Projects.StopProjectAsync(projectId);
        await harness.WaitForStateAsync(projectId, ProjectState.Stopped);
    }

    private static int WarningsAbout(LifecycleHarness harness, string text) => harness.Warnings.Count(line => line.Contains(text));

    // ── Skip-permissions ──

    /// <summary>
    /// Refused before anything is written: no folder, no script, no project, no launch. The schema's
    /// boolean may arrive as a string, as the dev root's <c>"default": "true"</c> once sent it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData("true")]
    public async Task CreateAskingForSkip_UnderARootThatDoesNotAllowIt_IsRefused_AndNothingIsCreated(object asked)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["prepare"] = "prepare" });
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "prepare.ps1"), "Set-Content -Path prepared.txt -Value ran");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(inputs: AskingForSkip(asked)));

        Assert.Contains("allowSkipPermissions", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Equal([".godmode-root"], Directory.GetFileSystemEntries(harness.RootPath).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData("true")]
    public async Task CreateAskingForSkip_UnderARootThatAllowsIt_LaunchesWithSkip_AndItsResumeKeepsIt(object asked)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(), rootConfig: AllowsSkip);

        var created = await harness.CreateProjectAsync(inputs: AskingForSkip(asked));
        var create = await harness.WaitForStdinAsync(created.Id);
        await StopAsync(harness, created.Id);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.Contains(Skip, create.Argv);
        Assert.Contains(Skip, resume.Argv);
    }

    /// <summary>A create that does not ask launches without it, whatever the root allows.</summary>
    [Fact]
    public async Task CreateNotAskingForSkip_UnderARootThatAllowsIt_LaunchesWithoutIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(), rootConfig: AllowsSkip);

        var created = await harness.CreateProjectAsync();

        Assert.DoesNotContain(Skip, (await harness.WaitForStdinAsync(created.Id)).Argv);
    }

    /// <summary>
    /// The launch reads the root's config when it launches, not when the create was checked: here a
    /// prepare script (the host editing its config while a create runs) turns the root's permission off.
    /// </summary>
    [Fact]
    public async Task Create_UnderARootThatStopsAllowingSkipBeforeItsLaunch_LaunchesWithoutIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["allowSkipPermissions"] = true, ["prepare"] = "disallow" });
        var configPath = Path.Combine(GodModeRoot(harness), "config.json");
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "disallowed.txt"), Edited(File.ReadAllText(configPath), c => c.Remove("allowSkipPermissions")));
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "disallow.ps1"),
            "Copy-Item -Path (Join-Path '.godmode-root' 'disallowed.txt') -Destination (Join-Path '.godmode-root' 'config.json') -Force");

        var created = await harness.CreateProjectAsync(inputs: AskingForSkip(true));
        var create = await harness.WaitForStdinAsync(created.Id);

        Assert.True(ProjectSettings.Load(harness.ProjectPath(created.Id)).DangerouslySkipPermissions, "the create did not ask for skip");
        Assert.DoesNotContain(Skip, create.Argv);
        Assert.Equal(1, WarningsAbout(harness, "asks to skip permissions"));
    }

    /// <summary>settings.json is the session's to write: asking there, under a root that does not allow it, changes nothing. Said once.</summary>
    [Fact]
    public async Task ProjectWhoseSettingsAskForSkip_UnderARootThatDoesNotAllowIt_ResumesWithoutIt_AndSaysSoOnce()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await StopAsync(harness, created.Id);
        EditSettings(harness, created.Id, settings => settings with { DangerouslySkipPermissions = true });

        await harness.Projects.ResumeProjectAsync(created.Id);
        var first = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        await StopAsync(harness, created.Id);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var second = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 2);

        Assert.DoesNotContain(Skip, first.Argv);
        Assert.DoesNotContain(Skip, second.Argv);
        Assert.Equal(1, WarningsAbout(harness, "asks to skip permissions"));
    }

    /// <summary>
    /// A project created with skip, under a root that has since stopped allowing it, carries on after
    /// a restart without it: the restart relaunches it unattended, from its own files.
    /// </summary>
    [Fact]
    public async Task ProjectCreatedWithSkip_UnderARootThatNoLongerAllowsIt_IsResumedAfterARestartWithoutIt()
    {
        await using var harness = new LifecycleHarness(Working(), rootConfig: AllowsSkip);
        var created = await harness.CreateProjectAsync(inputs: AskingForSkip(true));
        var create = await harness.WaitForStdinAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(created.Id).Contains("Working on it")), null,
            () => $"claude did not start working.\n{harness.Describe(created.Id)}");
        EditRootConfig(harness, config => config.Remove("allowSkipPermissions"));

        await harness.RestartAsync();
        var resume = await harness.WaitForStdinAsync(created.Id, index: 1);

        Assert.Contains(Skip, create.Argv);
        Assert.Equal(create.ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.DoesNotContain(Skip, resume.Argv);
        Assert.Equal(1, WarningsAbout(harness, "asks to skip permissions"));
    }

    // ── Permission mode ──

    /// <summary>An overlay's mode, kept with the project: its resume keeps it after the root has changed its own.</summary>
    [Fact]
    public async Task PermissionModeInAnOverlay_IsPassedOnCreateAndResume_EvenAfterTheRootConfigHasChanged()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var overlay = Path.Combine(GodModeRoot(harness), "config.work.json");
        File.WriteAllText(overlay, """{ "permissionMode": "auto" }""");

        var created = await harness.CreateProjectAsync();
        var create = await harness.WaitForStdinAsync(created.Id);
        await StopAsync(harness, created.Id);
        WriteRootFile(overlay, """{ "permissionMode": "plan" }""");
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.Equal("auto", create.ArgValue(Mode));
        Assert.Equal("auto", resume.ArgValue(Mode));
        Assert.Equal("auto", ProjectSettings.Load(harness.ProjectPath(created.Id)).PermissionMode);
    }

    /// <summary>A root with no mode passes none: claude's own settings decide, as before.</summary>
    [Fact]
    public async Task RootWithoutAPermissionMode_LaunchesWithoutOne()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());

        var created = await harness.CreateProjectAsync();

        Assert.DoesNotContain(Mode, (await harness.WaitForStdinAsync(created.Id)).Argv);
    }

    /// <summary>A mode the root may not pick is a config error: the create fails before anything is written.</summary>
    [Theory]
    [InlineData("bypassPermissions", "is refused")]
    [InlineData("yolo", "is not one of acceptEdits, auto, manual, dontAsk, plan")]
    public async Task PermissionModeTheRootMayNotPick_FailsTheCreate_AndNothingIsCreated(string mode, string reason)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "config.work.json"), JsonSerializer.Serialize(new { permissionMode = mode }));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync());

        Assert.Contains($"config.work.json: permissionMode '{mode}' {reason}", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Equal([".godmode-root"], Directory.GetFileSystemEntries(harness.RootPath).Select(Path.GetFileName));
    }

    /// <summary>The kept mode is checked again at each launch: a settings.json that asks for bypassPermissions gets no mode at all.</summary>
    [Fact]
    public async Task KeptPermissionModeOfBypassPermissions_LaunchesWithoutAMode_AndWithoutSkip()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await StopAsync(harness, created.Id);
        EditSettings(harness, created.Id, settings => settings with { PermissionMode = "bypassPermissions" });

        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.DoesNotContain(Mode, resume.Argv);
        Assert.DoesNotContain(Skip, resume.Argv);
        Assert.Equal(1, WarningsAbout(harness, "permissionMode 'bypassPermissions' is refused"));
    }

    /// <summary>Skipping permissions leaves no mode to apply: the mode is left out, with a warning.</summary>
    [Fact]
    public async Task SkipPermissions_OverridesThePermissionMode_WithAWarning()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["allowSkipPermissions"] = true, ["permissionMode"] = "auto" });

        var created = await harness.CreateProjectAsync(inputs: AskingForSkip(true));
        var create = await harness.WaitForStdinAsync(created.Id);

        Assert.Contains(Skip, create.Argv);
        Assert.DoesNotContain(Mode, create.Argv);
        Assert.Equal(1, WarningsAbout(harness, "so its permission mode auto is ignored"));
    }

    // ── What the client is told ──

    /// <summary>Each action says whether its root allows skip-permissions, the overlay's word over the base's.</summary>
    [Fact]
    public async Task ListProjectRoots_SaysWhichActionsAllowSkipPermissions()
    {
        await using var harness = new LifecycleHarness(new FakeScript(), rootConfig: AllowsSkip);
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "config.open.json"), "{}");
        File.WriteAllText(Path.Combine(GodModeRoot(harness), "config.closed.json"), """{ "allowSkipPermissions": false }""");

        var root = Assert.Single(await harness.Projects.ListProjectRootsAsync(), r => r.Name == LifecycleHarness.RootName);

        Assert.Equal(
            [("closed", false), ("open", true)],
            root.Actions!.Select(a => (a.Name, a.AllowSkipPermissions)).OrderBy(a => a.Name));
    }
}
