using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session's parent (#389): the session that started it, named at its create, kept in its
/// status.json and listed with it. A parent is a session of the same server, so one this server does
/// not track is refused before anything is created. It is metadata only: deleting the parent leaves its
/// children as they are, their parent ID naming a session that is gone.
/// </summary>
public class ParentTests
{
    private const string OtherRoot = "other";

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private static async Task<ProjectSummary> ListedAsync(LifecycleHarness harness, string projectId) =>
        (await harness.Projects.ListProjectsAsync()).Single(p => p.Id == projectId);

    [Fact]
    public async Task CreateWithParent_StoresItInStatusJson_AndTheList()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var parent = await harness.CreateProjectAsync("overseer");

        var child = await harness.CreateProjectAsync("worker", parentId: parent.Id);

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(parent.Id, harness.ReadStatusFile(child.Id).ParentId);
        Assert.Equal(parent.Id, (await ListedAsync(harness, child.Id)).ParentId);
        Assert.Null((await ListedAsync(harness, parent.Id)).ParentId);
        Assert.Null(harness.ReadStatusFile(parent.Id).ParentId);
    }

    [Fact]
    public async Task Parent_SurvivesAStop_AResume_AndARestart()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var parent = await harness.CreateProjectAsync("overseer");
        var child = await harness.CreateProjectAsync("worker", parentId: parent.Id);
        await harness.WaitForStdinAsync(child.Id);

        await harness.Projects.StopProjectAsync(child.Id);
        await harness.WaitForStateAsync(child.Id, ProjectState.Stopped);
        Assert.Equal(parent.Id, (await harness.Projects.GetStatusAsync(child.Id)).ParentId);
        await harness.Projects.ResumeProjectAsync(child.Id);
        await harness.WaitForLaunchAsync(child.Id, _ => true, index: 1);
        Assert.Equal(parent.Id, (await harness.Projects.GetStatusAsync(child.Id)).ParentId);

        await harness.RestartAsync();

        Assert.Equal(parent.Id, (await harness.Projects.GetStatusAsync(child.Id)).ParentId);
        Assert.Equal(parent.Id, (await ListedAsync(harness, child.Id)).ParentId);
        Assert.Equal(parent.Id, harness.ReadStatusFile(child.Id).ParentId);
    }

    [Fact]
    public async Task ParentOnAnotherRootOfTheServer_IsKept()
    {
        await using var harness = new LifecycleHarness(Waiting(), extraRoots: [(OtherRoot, LifecycleHarness.ProfileName)]);
        var parent = await harness.CreateProjectAsync("overseer", root: OtherRoot);

        var child = await harness.CreateProjectAsync("worker", parentId: parent.Id);

        Assert.Equal(parent.Id, (await ListedAsync(harness, child.Id)).ParentId);
        Assert.Equal(parent.Id, harness.ReadStatusFile(child.Id).ParentId);
    }

    [Fact]
    public async Task UnknownParent_IsRefused_AndNothingIsCreated()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var unknown = LifecycleHarness.PlantedId();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("worker", parentId: unknown));

        Assert.Contains($"The parent session '{unknown}' is not one this server has", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Equal([".godmode-root", "logs"], Directory.GetFileSystemEntries(harness.RootPath).Select(Path.GetFileName).Order());
    }

    /// <summary>Another server's session is one this server does not have, whatever its ID looks like.</summary>
    [Fact]
    public async Task ParentOnAnotherServer_IsRefused()
    {
        await using var other = new LifecycleHarness(Waiting());
        var elsewhere = await other.CreateProjectAsync("overseer");
        await using var harness = new LifecycleHarness(Waiting());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync("worker", parentId: elsewhere.Id));

        Assert.Contains("is not one this server has", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    [Fact]
    public async Task DeletingTheParent_LeavesTheChild_AsItIs_ThroughARestart()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var parent = await harness.CreateProjectAsync("overseer");
        var child = await harness.CreateProjectAsync("worker", parentId: parent.Id);
        await harness.WaitForStdinAsync(child.Id);

        await harness.Projects.DeleteProjectAsync(parent.Id, force: true);

        var listed = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal((child.Id, parent.Id), (listed.Id, listed.ParentId));
        Assert.True(LifecycleHarness.IsProcessAlive(harness.Tracked(child.Id).Process.ProcessId));

        await harness.RestartAsync();

        listed = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal((child.Id, parent.Id), (listed.Id, listed.ParentId));
    }

    /// <summary>Through the hub the parent is the <c>__parentId</c> input: CreateProject's four parameters stay as every caller passes them.</summary>
    [Fact]
    public async Task OverTheHub_TheParentInput_SetsTheParent_AndAnnouncesIt()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var parent = await harness.CreateProjectAsync("overseer");
        var connection = harness.Connect("app");

        var child = (await connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("worker", parent.Id))).Project!;

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(parent.Id, harness.ReadStatusFile(child.Id).ParentId);
        var created = harness.Hub.Pushes.Single(p => p.Method == nameof(Shared.Hubs.IProjectHubClient.ProjectCreated) && p.ProjectId == child.Id);
        Assert.Equal(parent.Id, created.Status!.ParentId);
    }

    [Fact]
    public async Task OverTheHub_ANullOrBlankParentInput_IsTopLevel()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var connection = harness.Connect("app");

        var none = (await connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("none", null))).Project!;
        var blank = (await connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("blank", " "))).Project!;

        Assert.Null(none.ParentId);
        Assert.Null(blank.ParentId);
    }

    [Fact]
    public async Task OverTheHub_AnUnknownOrNonStringParent_IsRefused_WithTheReason()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var connection = harness.Connect("app");

        var unknown = await Assert.ThrowsAsync<HubException>(() =>
            connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("worker", LifecycleHarness.PlantedId())));
        var inputs = Inputs("worker", null);
        inputs[CreateProjectRequest.ParentInput] = JsonSerializer.SerializeToElement(42);
        var number = await Assert.ThrowsAsync<HubException>(() =>
            connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, inputs));

        Assert.Contains("is not one this server has", unknown.Message);
        Assert.Contains("__parentId is not a session ID: 42", number.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// A parent whose root moves profile while the server is down takes a new ID at the restart, and its
    /// children, in its root (re-keyed with it) or another, take that ID as their parent, saved.
    /// </summary>
    [Fact]
    public async Task ParentsRootMovesProfile_AtARestart_ItsChildrenTakeItsNewId()
    {
        await using var harness = new LifecycleHarness(Waiting(), extraRoots: [(OtherRoot, LifecycleHarness.ProfileName)]);
        var parent = await harness.CreateProjectAsync("overseer", root: OtherRoot);
        var elsewhere = await harness.CreateProjectAsync("worker", parentId: parent.Id);
        var beside = await harness.CreateProjectAsync("helper", root: OtherRoot, parentId: parent.Id);
        var unrelated = await harness.CreateProjectAsync("alone");

        RenameProfile(Path.Combine(harness.RootsDir, OtherRoot), "renamed");
        await harness.RestartAsync();

        var renamedParent = Renamed(parent.Id);
        var renamedBeside = Renamed(beside.Id);
        Assert.Equal(renamedParent, (await harness.Projects.GetStatusAsync(renamedParent)).Id);
        Assert.Equal(renamedParent, (await ListedAsync(harness, elsewhere.Id)).ParentId);
        Assert.Equal(renamedParent, harness.ReadStatusFile(elsewhere.Id).ParentId);
        Assert.Equal(renamedParent, (await ListedAsync(harness, renamedBeside)).ParentId);
        Assert.Equal(renamedParent, harness.ReadStatusFile(renamedBeside).ParentId);
        Assert.Null((await ListedAsync(harness, unrelated.Id)).ParentId);
    }

    /// <summary>
    /// Live: a parent without a claude takes its new ID at once, and a child that is not re-keyed, its
    /// claude running, takes it too, saved and pushed as StatusChanged.
    /// </summary>
    [Fact]
    public async Task ParentsRootMovesProfile_Live_ARunningChildTakesItsNewId_AndIsPushed()
    {
        await using var harness = new LifecycleHarness(Waiting(), extraRoots: [(OtherRoot, LifecycleHarness.ProfileName)],
            settings: new Dictionary<string, string?> { [ProjectManager.RootsPollSetting] = "0.2" });
        await harness.Projects.RecoverProjectsAsync();
        var parent = await harness.CreateProjectAsync("overseer", root: OtherRoot);
        await harness.WaitForStdinAsync(parent.Id);
        await harness.Projects.StopProjectAsync(parent.Id);
        await harness.WaitForStateAsync(parent.Id, ProjectState.Stopped);
        var child = await harness.CreateProjectAsync("worker", parentId: parent.Id);
        await harness.WaitForStdinAsync(child.Id);

        RenameProfile(Path.Combine(harness.RootsDir, OtherRoot), "renamed");

        var renamedParent = Renamed(parent.Id);
        await harness.WaitForStatusPushAsync(child.Id, status => status.ParentId == renamedParent);
        Assert.Contains(harness.Hub.Pushes, p => p.Method == nameof(Shared.Hubs.IProjectHubClient.ProjectCreated) && p.ProjectId == renamedParent);
        Assert.Equal(renamedParent, harness.ReadStatusFile(child.Id).ParentId);
        Assert.Equal(renamedParent, (await ListedAsync(harness, child.Id)).ParentId);
        Assert.True(harness.Lifecycle.IsRunning(harness.Tracked(child.Id)), "the child's claude runs on");
    }

    /// <summary>
    /// The prepare and create scripts get the parent as GODMODE_PARENT_ID, whether it came as the hub's
    /// __parentId input or the request's ParentId, and the input is no GODMODE_INPUT_*. A top-level create has none.
    /// </summary>
    [Fact]
    public async Task Scripts_GetGodModeParentId_AndNoParentInput()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["prepare"] = "told.ps1", ["create"] = "told.ps1" });
        var told = Path.Combine(harness.WorkDir, "told.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "told.ps1"), $$"""
            $inputs = (Get-ChildItem env: | Where-Object Name -like 'GODMODE_INPUT_*' | ForEach-Object Name | Sort-Object) -join ','
            Add-Content -Path '{{told}}' -Value "$env:GODMODE_INPUT_NAME parent=[$env:GODMODE_PARENT_ID] inputs=[$inputs]"
            """);
        var parent = await harness.CreateProjectAsync("overseer");
        var typed = await harness.CreateProjectAsync("typed", parentId: parent.Id);
        var connection = harness.Connect("app");
        var hub = (await connection.CreateProjectAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, null, Inputs("hub", parent.Id))).Project!;

        string Told(string name) => $"{name} parent=[{(name == "overseer" ? "" : parent.Id)}] inputs=[GODMODE_INPUT_NAME,GODMODE_INPUT_PROMPT]";
        Assert.Equal([Told("overseer"), Told("overseer"), Told("typed"), Told("typed"), Told("hub"), Told("hub")],
            File.ReadAllLines(told).Select(line => line.Trim()).Where(line => line != ""));
        Assert.Equal((parent.Id, parent.Id), (typed.ParentId, hub.ParentId));
    }

    private static string Renamed(string id) => "renamed" + id[LifecycleHarness.ProfileName.Length..];

    /// <summary>Sets the root's profileName in its config.json, as a save of it by hand would.</summary>
    private static void RenameProfile(string rootPath, string profile)
    {
        var config = Path.Combine(rootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["profileName"] = JsonSerializer.SerializeToElement(profile);
        File.WriteAllText(config, JsonSerializer.Serialize(edited));
    }

    private static Dictionary<string, JsonElement> Inputs(string name, string? parentId) => new()
    {
        ["name"] = JsonSerializer.SerializeToElement(name),
        ["prompt"] = JsonSerializer.SerializeToElement("Start"),
        [CreateProjectRequest.ParentInput] = JsonSerializer.SerializeToElement(parentId),
    };
}
