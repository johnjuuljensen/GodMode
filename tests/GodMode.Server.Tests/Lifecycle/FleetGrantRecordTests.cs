using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// What a session was started as, for its fleet tools, is the server's record (<see cref="FleetGrantFile"/>, in the
/// root's logs), never the session's own files: a session that renames its action in its settings.json is still
/// what it was created as after a restart, while a granted one keeps its grant. The grant is the root's config's:
/// one that cannot be read, or that no longer has the action, grants nothing.
/// </summary>
public class FleetGrantRecordTests
{
    private const string Work = "work";
    private const string Overseer = "overseer";
    private const string Grantable = "grantable";

    private static FakeScript Working() => new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it").AwaitStdin();

    private static LifecycleHarness Harness()
    {
        var harness = new LifecycleHarness(Working());
        WriteAction(harness, Work, "{}");
        WriteAction(harness, Overseer, """{ "fleetTools": true }""");
        WriteAction(harness, Grantable, """{ "fleetTools": "grantable" }""");
        return harness;
    }

    private static void WriteAction(LifecycleHarness harness, string action, string json) =>
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", $"config.{action}.json"), json);

    private static async Task<string> CreateAsync(LifecycleHarness harness, string action, string name, bool fleetTools = false)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["prompt"] = JsonSerializer.SerializeToElement("Start"),
        };
        var status = (await harness.Projects.CreateProjectAsync(new CreateProjectRequest(
            LifecycleHarness.ProfileName, LifecycleHarness.RootName, inputs, action, FleetTools: fleetTools))).Project!;
        await harness.WaitForStdinAsync(status.Id);
        return status.Id;
    }

    private static string[] McpServersOf(FakeLaunch launch) =>
        JsonDocument.Parse(launch.McpConfig!).RootElement.GetProperty("mcpServers").EnumerateObject().Select(s => s.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task ASessionThatRenamesItsActionInItsSettings_IsWhatItWasCreatedAs_AfterARestart()
    {
        await using var harness = Harness();
        var worker = await CreateAsync(harness, Work, "worker");
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        Assert.False(harness.Projects.HasFleetTools(worker));
        Assert.True(harness.Projects.HasFleetTools(overseer));

        // settings.json is the session's to write: it names the overseer action, and asks for the tools
        var settingsPath = Path.Combine(harness.StatePath(worker), "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["actionName"] = Overseer;
        settings["fleetTools"] = true;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        await harness.RestartAsync();

        Assert.False(harness.Projects.HasFleetTools(worker));
        Assert.True(harness.Projects.HasFleetTools(overseer));
        Assert.Equal(["godmode"], McpServersOf(await harness.WaitForStdinAsync(worker, index: 1)));
        Assert.Equal(["godmode", "godmode-fleet"], McpServersOf(await harness.WaitForStdinAsync(overseer, index: 1)));
    }

    [Fact]
    public async Task AGrantedChild_KeepsItsGrantOverARestart_AndAGrantableActionsOtherSessionHasNone()
    {
        await using var harness = Harness();
        var granted = await CreateAsync(harness, Grantable, "epic", fleetTools: true);
        var plain = await CreateAsync(harness, Grantable, "plain");

        Assert.Equal(["godmode", "godmode-fleet"], McpServersOf(harness.Launches(granted)[0]));
        Assert.Equal(["godmode"], McpServersOf(harness.Launches(plain)[0]));

        await harness.RestartAsync();

        Assert.True(harness.Projects.HasFleetTools(granted));
        Assert.False(harness.Projects.HasFleetTools(plain));
    }

    [Fact]
    public async Task AGrant_OfAnActionThatAllowsNone_IsRefused_BeforeAnythingIsMade()
    {
        await using var harness = Harness();
        var inputs = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("w") };

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.Projects.CreateProjectAsync(
            new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName, inputs, Work, FleetTools: true)));

        Assert.Contains("\"fleetTools\": \"grantable\"", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(harness.RootPath, "logs"), "*" + FleetGrantFile.Extension));
    }

    [Fact]
    public async Task ARootConfigThatCannotBeRead_OrHasNoLongerTheAction_GrantsNothing()
    {
        await using var harness = Harness();
        var overseer = await CreateAsync(harness, Overseer, "overseer");

        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json"), "{ not json");
        Assert.False(harness.Projects.HasFleetTools(overseer));

        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json"),
            JsonSerializer.Serialize(new { profileName = LifecycleHarness.ProfileName }));
        File.Delete(Path.Combine(harness.RootPath, ".godmode-root", $"config.{Overseer}.json"));
        Assert.False(harness.Projects.HasFleetTools(overseer));
    }

    /// <summary>The record is the server's: one that cannot be read, or none (a session made before it was kept), grants nothing.</summary>
    [Fact]
    public async Task ASessionWithoutARecord_HasNoGrant()
    {
        await using var harness = Harness();
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        var record = FleetGrantFile.PathFor(harness.RootPath, overseer.Split('/')[^1]);
        Assert.True(File.Exists(record));

        File.WriteAllText(record, "not json");
        Assert.False(harness.Projects.HasFleetTools(overseer));

        File.Delete(record);
        Assert.False(harness.Projects.HasFleetTools(overseer));
    }

    /// <summary>The record is the session's that was created in its folder: one naming another folder grants the session nothing.</summary>
    [Fact]
    public async Task ARecordOfAnotherFolder_GrantsNothing()
    {
        await using var harness = Harness();
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        var sessionId = overseer.Split('/')[^1];
        var grant = FleetGrantFile.Read(harness.RootPath, sessionId)!;
        Assert.Equal(Path.GetFileName(harness.ProjectPath(overseer)), grant.Folder);

        FleetGrantFile.Write(harness.RootPath, sessionId, grant with { Folder = "elsewhere" });

        Assert.False(harness.Projects.HasFleetTools(overseer));
    }

    /// <summary>A delete or a forget takes the record with it: a restored or planted session under the id finds none.</summary>
    [Fact]
    public async Task ADeleteOrAForget_DeletesTheRecord()
    {
        await using var harness = Harness();
        var deleted = await CreateAsync(harness, Overseer, "deleted");
        var forgotten = await CreateAsync(harness, Overseer, "forgotten");

        await harness.Projects.DeleteProjectAsync(deleted);
        await harness.Projects.ForgetProjectAsync(forgotten);

        Assert.False(File.Exists(FleetGrantFile.PathFor(harness.RootPath, deleted.Split('/')[^1])));
        Assert.False(File.Exists(FleetGrantFile.PathFor(harness.RootPath, forgotten.Split('/')[^1])));
    }

    [Fact]
    public async Task ARelaunch_AfterTheActionStopsGranting_HasNoFleetEntry()
    {
        await using var harness = Harness();
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        Assert.Equal(["godmode", "godmode-fleet"], McpServersOf(harness.Launches(overseer)[0]));

        WriteAction(harness, Overseer, "{}");
        await harness.Projects.StopProjectAsync(overseer);
        await harness.WaitForStateAsync(overseer, ProjectState.Stopped);
        await harness.Projects.ReplyAndResumeAsync(overseer, "Go on");

        Assert.Equal(["godmode"], McpServersOf(await harness.WaitForStdinAsync(overseer, index: 1)));
    }

    /// <summary>A launch's token opens nothing once its process has exited, whatever was left of its config file.</summary>
    [Fact]
    public async Task TheToken_IsClearedWhenItsProcessExits()
    {
        await using var harness = Harness();
        var overseer = await CreateAsync(harness, Overseer, "overseer");
        var token = GodModeMcpEntry.FleetOf(harness.Launches(overseer)[0]).Token;
        Assert.NotNull(harness.Projects.ValidateProjectToken(overseer, token));

        await harness.Projects.StopProjectAsync(overseer);
        await harness.WaitForStateAsync(overseer, ProjectState.Stopped);

        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(harness.Projects.ValidateProjectToken(overseer, token) == null)),
            "the exited launch's token still opens the endpoint");
    }

    /// <summary>
    /// A session with the fleet's tools keeps its MCP config, and the token that opens them, out of its working folder,
    /// which in a shared folder its neighbours work in: beside its record, in the root's logs, until its process exits.
    /// </summary>
    [Fact]
    public async Task AGrantedSessionsMcpConfig_IsOutOfItsWorkingFolder_EvenWhereItSharesIt()
    {
        await using var harness = Harness();
        WriteAction(harness, "workspace", """{ "fleetTools": true, "sharedFolder": true }""");
        var overseer = await CreateAsync(harness, "workspace", "overseer");
        var worker = await CreateAsync(harness, Work, "worker");
        var sessionId = overseer.Split('/')[^1];

        var outside = McpConfigFile.PathFor(harness.RootPath, sessionId);
        Assert.Equal(["godmode", "godmode-fleet"], McpServersOf(harness.Launches(overseer)[0]));
        Assert.True(File.Exists(outside));
        Assert.Empty(Directory.GetFiles(harness.ProjectPath(overseer), McpConfigFile.FileName, SearchOption.AllDirectories));
        // An ungranted session's is out of its folder too: its token speaks for it (message_parent)
        Assert.True(File.Exists(harness.McpConfigPath(worker)));
        Assert.Empty(Directory.GetFiles(harness.ProjectPath(worker), McpConfigFile.FileName, SearchOption.AllDirectories));

        await harness.Projects.StopProjectAsync(overseer);
        await harness.WaitForStateAsync(overseer, ProjectState.Stopped);
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(!File.Exists(outside))), "the config outlived its process");
    }
}
