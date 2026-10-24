using GodMode.FakeClaude;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Hiding a session (issue #598): the user's, kept in its settings.json as its importance is, pushed to every client, and
/// read back by a recovery.
/// </summary>
public class HiddenTests
{
    [Fact]
    public async Task SetHidden_KeepsItInSettings_PushesTheStatusAndTheList_AndARestartKeepsIt()
    {
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it.").Sleep(50).EmitResult("done").AwaitStdin());
        var session = await harness.CreateProjectAsync("session");
        await harness.WaitForStatusPushAsync(session.Id, status => status.LastResult == "done" && status.State == ProjectState.Idle);

        await harness.Projects.SetHiddenAsync(session.Id, true);

        Assert.True(ProjectFiles.ProjectSettings.Load(harness.StatePath(session.Id)).Hidden);
        await harness.WaitForStatusPushAsync(session.Id, status => status.Hidden);
        Assert.True((await harness.Projects.ListProjectsAsync()).Single(s => s.Id == session.Id).Hidden);

        await harness.RestartAsync(resume: false);
        Assert.True(harness.Tracked(session.Id).Status.Hidden);

        await harness.Projects.SetHiddenAsync(session.Id, false);
        Assert.False(ProjectFiles.ProjectSettings.Load(harness.StatePath(session.Id)).Hidden);
        await harness.WaitForStatusPushAsync(session.Id, status => !status.Hidden);
    }
}
