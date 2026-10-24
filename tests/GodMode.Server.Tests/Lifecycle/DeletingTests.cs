using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A delete that takes a while (#599): the session shows it is being deleted from its start, a second delete meanwhile
/// is refused as such rather than as not found, and a folder that will not go once the session has left the list still
/// ends the delete, with ProjectDeleted and what was left.
/// </summary>
public sealed class DeletingTests
{
    [Fact]
    public async Task ASecondDelete_WhileTheFirstRuns_IsAlreadyBeingDeleted()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."),
            rootConfig: new Dictionary<string, object> { ["delete"] = "wait.ps1" });
        var started = Path.Combine(harness.RootPath, "delete-started");
        var gate = Path.Combine(harness.RootPath, "delete-gate");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "wait.ps1"),
            $"Set-Content -Path '{started}' -Value x; $until = (Get-Date).AddMinutes(1); " +
            $"while (-not (Test-Path '{gate}') -and (Get-Date) -lt $until) {{ Start-Sleep -Milliseconds 50 }}");
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        var first = harness.Projects.DeleteProjectAsync(created.Id);
        await harness.WaitForStatusPushAsync(created.Id, s => s.Deleting);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(File.Exists(started)), null,
            () => $"the delete script did not start.\n{harness.Describe(created.Id)}");
        var second = harness.Projects.DeleteProjectAsync(created.Id);
        File.WriteAllText(gate, "");
        await first;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Contains("already being deleted", refused.Message);
    }

    [Fact]
    public async Task AFolderThatWillNotGo_AfterTheSessionLeftTheList_StillSendsProjectDeleted()
    {
        // A file held open blocks its folder's removal on Windows only
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var folder = harness.ProjectPath(created.Id);
        var client = harness.Connect("c1");

        using (new FileStream(Path.Combine(folder, "held.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await client.DeleteProjectAsync(created.Id);
            Assert.Contains(folder, result.Left);
        }

        Assert.Contains(harness.Hub.Pushes, p => p.Method == nameof(IProjectHubClient.ProjectDeleted) && p.ProjectId == created.Id);
        Assert.Null(((Services.ProjectManager)harness.Projects).Tracked(created.Id));
    }
}
