using System.Text.Json;
using GodMode.FakeClaude;
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

    private static Dictionary<string, JsonElement> Inputs(string name, string? parentId) => new()
    {
        ["name"] = JsonSerializer.SerializeToElement(name),
        ["prompt"] = JsonSerializer.SerializeToElement("Start"),
        [CreateProjectRequest.ParentInput] = JsonSerializer.SerializeToElement(parentId),
    };
}
