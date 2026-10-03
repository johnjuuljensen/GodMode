using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A create that fails before its launch leaves an Error session with no state and no claude (issue #448). It takes
/// no input: a reply, input, the fleet's send, a resume and a stop are refused with what is left to do, and nothing is
/// written into the folder the create claimed. Its delete runs no delete script for a folder that was never made.
/// </summary>
public class FailedCreateTests
{
    private static LifecycleHarness Harness(string createScript)
    {
        var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object>
            {
                ["scriptsCreateFolder"] = true,
                ["create"] = "create.ps1",
                ["delete"] = "delete.ps1",
            });
        var scripts = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(scripts, "create.ps1"), createScript);
        File.WriteAllText(Path.Combine(scripts, "delete.ps1"),
            "Set-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'delete-ran.txt') -Value $env:GODMODE_PROJECT_PATH");
        return harness;
    }

    private static string DeleteMarker(LifecycleHarness harness) => Path.Combine(harness.RootPath, "delete-ran.txt");

    [Fact]
    public async Task FailedCreate_TakesNoInput_WritesNothing_AndDeletesWithoutItsScript()
    {
        await using var harness = Harness("throw 'no worktree for you'");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync("p1"));
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        var status = await harness.Projects.GetStatusAsync(project.Id);
        Assert.Equal(ProjectState.Error, status.State);
        Assert.True(status.CreateFailed);
        Assert.Contains("no worktree for you", status.LastError);
        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal(AttentionKind.Error, item.Kind);
        Assert.True(item.CreateFailed);
        Assert.Contains("no worktree for you", item.Text);
        var folder = Path.Combine(harness.RootPath, "p1");

        var refused = await Assert.ThrowsAsync<CreateFailedException>(() => harness.Projects.ReplyAndResumeAsync(project.Id, "Try again"));
        Assert.Contains("delete it, or create it again", refused.Message);
        await Assert.ThrowsAsync<CreateFailedException>(() => harness.Projects.SendInputAsync(project.Id, "Try again"));
        await Assert.ThrowsAsync<CreateFailedException>(() => harness.Projects.SendOrHoldAsync(project.Id, "Try again", senderId: null));
        await Assert.ThrowsAsync<CreateFailedException>(() => harness.Projects.ResumeProjectAsync(project.Id));
        await Assert.ThrowsAsync<CreateFailedException>(() => harness.Projects.StopProjectAsync(project.Id));

        Assert.False(Directory.Exists(folder), $"{folder}, which the create never made, was written to");
        Assert.Equal(0, harness.LaunchesAsked(project.Id));
        var logs = Path.Combine(harness.RootPath, "logs");
        Assert.Empty(Directory.Exists(logs) ? Directory.GetFiles(logs, "*.inbox.jsonl") : []);
        Assert.Equal(ProjectState.Error, (await harness.Projects.GetStatusAsync(project.Id)).State);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Empty(harness.Projects.GetAttention());
        Assert.False(File.Exists(DeleteMarker(harness)), "the delete script ran for a folder that was never made");
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>A create that made its folder before it failed has its delete script take the folder down, as any delete does.</summary>
    [Fact]
    public async Task FailedCreate_ThatMadeItsFolder_RunsTheDeleteScript()
    {
        await using var harness = Harness("New-Item -ItemType Directory -Force $env:GODMODE_PROJECT_PATH | Out-Null; throw 'half made'");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateProjectAsync("p1"));
        var project = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.True((await harness.Projects.GetStatusAsync(project.Id)).CreateFailed);

        await harness.Projects.DeleteProjectAsync(project.Id);

        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.True(File.Exists(DeleteMarker(harness)), "the delete script did not run for the folder the create made");
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "p1")));
    }
}
