using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// An action that starts no session (<c>"session": false</c>): its scripts run in the root as a
/// create's do, with the root's environment and inputs, and nothing is tracked, no folder made and no
/// claude started. The fixture is a provisioning root whose action makes a sibling root in the scan
/// folder, as "New experiment root" does, and says so in its result file's <c>message</c>.
/// </summary>
public sealed class SessionlessActionTests
{
    /// <summary>The provisioning root's config: its one action starts no session and runs <see cref="ProvisionScript"/>.</summary>
    private static readonly Dictionary<string, object> Provisioning = new() { ["session"] = false, ["create"] = "provision.ps1" };

    /// <summary>
    /// Makes the root named by the input <c>name</c> beside the provisioning root, in the profile the
    /// input names, and says what it made. It gives a <c>project_path</c> too, the new root, outside the
    /// provisioning root, which a create's checks would refuse: nothing here is a project's folder.
    /// </summary>
    private const string ProvisionScript = """
        $ErrorActionPreference = 'Stop'
        $target = Join-Path (Split-Path $env:GODMODE_ROOT_PATH -Parent) $env:GODMODE_INPUT_NAME
        New-Item -ItemType Directory -Force (Join-Path $target '.godmode-root') | Out-Null
        Set-Content -Path (Join-Path $target '.godmode-root/config.json') -Value ('{ "profileName": "' + $env:GODMODE_INPUT_PROFILE + '", "description": "made by provisioning" }')
        Write-Output "made $target"
        Write-Output "session=[$env:GODMODE_SESSION_ID] folder=[$env:GODMODE_PROJECT_PATH] fake=[$env:GODMODE_FAKE_CLAUDE_SCRIPT]"
        if ($env:GODMODE_INPUT_FAIL) { throw 'provisioning failed' }
        Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$target`nkind=experiment`nmessage=Root $env:GODMODE_INPUT_NAME is ready"
        """;

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private static async Task<LifecycleHarness> ProvisioningHarnessAsync()
    {
        var harness = new LifecycleHarness(Waiting(), rootConfig: Provisioning,
            settings: new Dictionary<string, string?> { [ProjectManager.RootsPollSetting] = "0" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "provision.ps1"), ProvisionScript);
        await harness.Projects.RecoverProjectsAsync();
        return harness;
    }

    private static Dictionary<string, JsonElement> Inputs(string name, bool fail = false)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["profile"] = JsonSerializer.SerializeToElement("experiments"),
        };
        if (fail) inputs["fail"] = JsonSerializer.SerializeToElement("yes");
        return inputs;
    }

    /// <summary>Every file under the harness's work dir that a fake claude recorded to.</summary>
    private static string[] FakeRecordings(LifecycleHarness harness) =>
        Directory.GetFiles(harness.WorkDir, "fake-claude-*.jsonl", SearchOption.AllDirectories);

    [Fact]
    public async Task SessionlessAction_RunsItsScript_TracksNothing_AndStartsNoClaude()
    {
        await using var harness = await ProvisioningHarnessAsync();
        var client = harness.Connect("c1");

        var result = await client.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("fresh"));

        Assert.Null(result.Project);
        Assert.True(File.Exists(Path.Combine(harness.RootsDir, "fresh", ".godmode-root", "config.json")), "the script did not make the root");
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.DoesNotContain(harness.Hub.Pushes, push => push.Method == nameof(IProjectHubClient.ProjectCreated));
        // No working folder, no session state, and no claude
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "fresh")), "a working folder was made");
        Assert.Empty(Directory.GetDirectories(harness.RootsDir, ".godmode", SearchOption.AllDirectories));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Empty(FakeRecordings(harness));
    }

    /// <summary>
    /// The result file's message is the create's answer, and the script's output streams as progress,
    /// under the run's own id; the run's log is its own, under that id. The script had the root's
    /// environment, and no session's.
    /// </summary>
    [Fact]
    public async Task ItsMessage_ReachesTheClient_AndItsOutputStreamsAsProgress()
    {
        await using var harness = await ProvisioningHarnessAsync();
        var client = harness.Connect("c1");

        var result = await client.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("fresh"));

        Assert.Equal("Root fresh is ready", result.Message);
        var progress = client.Received.Where(push => push.Method == nameof(IProjectHubClient.CreationProgress)).ToArray();
        Assert.Contains(progress, push => push.Message!.StartsWith("made "));
        var runId = Assert.Single(progress.Select(push => push.ProjectId).Distinct())!;
        Assert.Matches(LifecycleHarness.IdPattern("fresh"), runId);
        var log = File.ReadAllText(Path.Combine(harness.RootPath, "logs", $"{runId.Split('/')[^1]}.log"));
        Assert.Contains($"session=[] folder=[] fake=[{harness.ScriptPath}]", log);
    }

    [Fact]
    public async Task TheRootItCreated_IsListedAfterTheNextRootsChanged()
    {
        await using var harness = await ProvisioningHarnessAsync();

        await harness.Connect("c1").CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("fresh"));

        // The poll is off: the run itself reads the roots again once its script has run
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.Hub.RootsPushes.Any(push => push.Roots!.Any(root => root.Name == "fresh"))), null,
            () => $"no RootsChanged listed the new root; pushed: {harness.Hub.RootsPushes.Count}\n{string.Join("\n", harness.Warnings)}");
        var made = Assert.Single(await harness.Projects.ListProjectRootsAsync(), root => root.Name == "fresh");
        Assert.Equal(("experiments", "made by provisioning"), (made.ProfileName, made.Description));
    }

    [Fact]
    public async Task AFailingScript_FailsTheCreate_AndLeavesNoProject()
    {
        await using var harness = await ProvisioningHarnessAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Connect("c1").CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("broken", fail: true)));

        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.DoesNotContain(harness.Hub.Pushes, push => push.Method == nameof(IProjectHubClient.ProjectCreated));
    }

    /// <summary>The app is told the action starts no session, and is offered no model or skip-permissions for it.</summary>
    [Fact]
    public async Task TheActionIsListed_AsStartingNoSession()
    {
        var config = new Dictionary<string, object>(Provisioning) { ["model"] = "opus", ["allowSkipPermissions"] = true };
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: config);

        var action = Assert.Single(Assert.Single(await harness.Projects.ListProjectRootsAsync()).Actions!);

        Assert.Equal((false, null, false), (action.Session, action.Model, action.AllowSkipPermissions));
    }

    /// <summary>What would give an action that starts no session a working folder, or nothing to run, is a config error.</summary>
    [Theory]
    [InlineData("""{ "session": false, "create": "provision.ps1", "sharedFolder": true }""", "sharedFolder")]
    [InlineData("""{ "session": false, "create": "provision.ps1", "scriptsCreateFolder": true }""", "scriptsCreateFolder")]
    [InlineData("""{ "session": false }""", "no create script")]
    public async Task NonsenseCombinations_AreRefusedAtConfigRead(string overlay, string named)
    {
        await using var harness = new LifecycleHarness(Waiting());
        var godModeRoot = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(godModeRoot, "config.provision.json"), overlay);
        File.WriteAllText(Path.Combine(godModeRoot, "provision.ps1"), ProvisionScript);

        var refused = Assert.Throws<InvalidDataException>(() => new RootConfigReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RootConfigReader>.Instance).ReadConfigStrict(harness.RootPath));
        Assert.Contains(named, refused.Message);
        Assert.Contains("'provision'", refused.Message);

        var create = await Assert.ThrowsAsync<ArgumentException>(() => harness.Projects.CreateProjectAsync(
            new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName, Inputs("fresh"), "provision")));
        Assert.Contains(named, create.Message);
        Assert.False(Directory.Exists(Path.Combine(harness.RootsDir, "fresh")));
    }

    /// <summary>The same flags on an action that starts a session are its own business, beside one that starts none.</summary>
    [Fact]
    public async Task ARootMayMixBoth_EachActionAsItsConfigSays()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var godModeRoot = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(godModeRoot, "config.provision.json"), """{ "session": false, "create": "provision.ps1" }""");
        File.WriteAllText(Path.Combine(godModeRoot, "config.chat.json"), """{ "sharedFolder": true }""");
        File.WriteAllText(Path.Combine(godModeRoot, "provision.ps1"), ProvisionScript);
        await harness.Projects.RecoverProjectsAsync();

        var ran = await harness.Projects.CreateProjectAsync(
            new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName, Inputs("fresh"), "provision"));
        var created = await harness.Projects.CreateProjectAsync(new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName,
            new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("talk"), ["prompt"] = JsonSerializer.SerializeToElement("hi") }, "chat"));

        Assert.Equal((null, "Root fresh is ready"), (ran.Project, ran.Message));
        Assert.NotNull(created.Project);
        Assert.Null(created.Message);
        try { await harness.WaitForStdinAsync(created.Project.Id); }
        finally { await harness.Projects.StopProjectAsync(created.Project.Id); }
    }
}
