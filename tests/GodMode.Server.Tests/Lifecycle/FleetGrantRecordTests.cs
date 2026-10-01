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
}
