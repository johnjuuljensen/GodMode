using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Issue #596: a <c>rate_limit_event</c> rejection followed by the turn's error result is the account's limit stopping the
/// session, not a turn that finished or failed. The session is held (<see cref="ProjectStatus.RateLimited"/>, on disk), and
/// the server sends it on at the reset, unless the user stopped it; a restart keeps the schedule. The account tells of its
/// limit once, whichever of its sessions it stops, and what the user sends meanwhile waits for the reset. The harness's
/// sessions share one account: none sets <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
public class RateLimitTests
{
    /// <summary>The server sends a session on at its limit's reset, with no margin: the resets here are seconds away.</summary>
    private static readonly IReadOnlyDictionary<string, string?> NoMargin = new Dictionary<string, string?> { [ProjectManager.ResumeMarginSetting] = "0" };

    private static DateTimeOffset In(TimeSpan time) => DateTimeOffset.UtcNow + time;

    /// <summary>A reset the test never reaches.</summary>
    private static readonly TimeSpan Later = TimeSpan.FromHours(3);

    /// <summary>The prompt's turn is stopped by the limit, resetting at <paramref name="resetsAt"/>; the next input's turn answers "carried on".</summary>
    private static FakeScript StoppedThenCarriesOn(DateTimeOffset resetsAt) =>
        new FakeScript().EmitInit().AwaitStdin().EmitUser("Say hello", echo: true).EmitRateLimitStop(resetsAt)
            .AwaitStdin().EmitUser("carry on", echo: true).EmitAssistant("Carrying on.").Sleep(50).EmitResult("carried on")
            .AwaitStdin();

    private static Task<ProjectStatus> WaitForLimitedAsync(LifecycleHarness harness, string projectId) =>
        harness.WaitForStatusPushAsync(projectId, status => status is { State: ProjectState.Idle, RateLimited.Stopped: true });

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        LifecycleHarness.WaitUntilAsync(
            async () => (await harness.Projects.GetStatusAsync(projectId)) is { State: ProjectState.Idle } status && status.LastResult == result,
            null, () => $"the turn that answers '{result}' did not end.\n{harness.Describe(projectId)}");

    [Fact]
    public async Task Rejection_MarksTheSessionLimited_UntilTheReset_AndRaisesNoFinishedOrError()
    {
        var resetsAt = In(Later);
        await using var harness = new LifecycleHarness(StoppedThenCarriesOn(resetsAt));
        var created = await harness.CreateProjectAsync();

        var limited = await WaitForLimitedAsync(harness, created.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAt.ToUnixTimeSeconds()).UtcDateTime, limited.RateLimited!.Until);
        Assert.Equal("five_hour", limited.RateLimited.Window);
        Assert.Null(limited.LastResult);
        Assert.Null(limited.LastError);
        // On disk, with the session's state: the schedule a restart keeps
        Assert.Equal(limited.RateLimited, harness.ReadStatusFile(created.Id).RateLimited);
        // The account's limit is told, once; the turn is neither finished nor failed
        var item = Assert.Single(harness.Projects.GetAttention(), i => i.ProjectId == created.Id);
        Assert.Equal(AttentionKind.RateLimited, item.Kind);
        Assert.Contains("five-hour limit", item.Text);
        Assert.DoesNotContain(harness.Hub.AttentionPushes.SelectMany(items => items),
            i => i.ProjectId == created.Id && i.Kind is AttentionKind.Finished or AttentionKind.Error);
    }

    [Fact]
    public async Task AtTheReset_TheServerSendsTheSessionTheContinuation()
    {
        await using var harness = new LifecycleHarness(StoppedThenCarriesOn(In(TimeSpan.FromSeconds(2))), settings: NoMargin);
        var created = await harness.CreateProjectAsync();
        await WaitForLimitedAsync(harness, created.Id);

        var continued = await harness.WaitForStdinAsync(created.Id, count: 2);
        Assert.Contains("it has reset. Carry on from where you were.", continued.Stdin[1]);
        await WaitForResultAsync(harness, created.Id, "carried on");
        Assert.Null((await harness.Projects.GetStatusAsync(created.Id)).RateLimited);
        // Its reset is told by the session that told its hit, until the turn it carried on with, which is newer, ended
        Assert.Contains("has reset", (await harness.Projects.GetStatusAsync(created.Id)).AccountNotice?.Text);
        Assert.Contains(harness.Hub.AttentionPushes, items => items.Any(i => i.Kind == AttentionKind.RateLimited && i.Text.Contains("has reset")));
    }

    [Fact]
    public async Task AStopByTheUser_CancelsTheResume()
    {
        // Far enough off for the stop to come first on a loaded machine, and waited past below
        var resetsAt = In(TimeSpan.FromSeconds(8));
        await using var harness = new LifecycleHarness(StoppedThenCarriesOn(resetsAt), settings: NoMargin);
        var created = await harness.CreateProjectAsync();
        await WaitForLimitedAsync(harness, created.Id);

        await harness.Projects.StopProjectAsync(created.Id);
        Assert.Null((await harness.Projects.GetStatusAsync(created.Id)).RateLimited);
        Assert.Null(harness.ReadStatusFile(created.Id).RateLimited);

        await Task.Delay(resetsAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
        Assert.Single(harness.Launches(created.Id));
        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Fact]
    public async Task ARestart_KeepsTheSchedule_AndTheStoppedSessionIsResumedWithTheContinuation()
    {
        await using var harness = new LifecycleHarness(StoppedThenCarriesOn(In(TimeSpan.FromSeconds(6))), settings: NoMargin);
        var created = await harness.CreateProjectAsync();
        var limited = await WaitForLimitedAsync(harness, created.Id);

        harness.UseScript(new FakeScript().EmitInit().AwaitStdin().EmitUser("carry on", echo: true).EmitAssistant("Carrying on.")
            .Sleep(50).EmitResult("carried on").AwaitStdin());
        await harness.RestartAsync();
        var recovered = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Stopped, recovered.State);
        Assert.Equal(limited.RateLimited, recovered.RateLimited);

        // Not resumed by the start: at the reset, with the continuation
        var resumed = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Contains("it has reset. Carry on from where you were.", resumed.Stdin[0]);
        await WaitForResultAsync(harness, created.Id, "carried on");
        Assert.Null((await harness.Projects.GetStatusAsync(created.Id)).RateLimited);
    }

    [Fact]
    public async Task TwoSessionsStopped_UnderOneAccount_AreToldOfOnce()
    {
        // Both are at work when the limit comes: each turn waits at the gate, then is stopped by it
        var gate = Path.Combine(Path.GetTempPath(), $"godmode-ratelimit-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitUser("Say hello", echo: true)
            .AwaitFile(gate).EmitRateLimitStop(In(Later)).AwaitStdin());
        try
        {
            var first = await harness.CreateProjectAsync("first");
            var second = await harness.CreateProjectAsync("second");
            await harness.WaitForStdinAsync(first.Id);
            await harness.WaitForStdinAsync(second.Id);
            File.WriteAllText(gate, "");
            await WaitForLimitedAsync(harness, first.Id);
            await WaitForLimitedAsync(harness, second.Id);

            var item = Assert.Single(harness.Projects.GetAttention());
            Assert.Equal(AttentionKind.RateLimited, item.Kind);
            Assert.Single(new[] { first.Id, second.Id }, id => harness.ReadStatusFile(id).AccountNotice != null);
        }
        finally
        {
            File.Delete(gate);
        }
    }

    [Fact]
    public async Task WhatTheUserSendsToALimitedAccount_IsHeldForTheReset()
    {
        // The first session's rejection limits the account; the second, idle with no prompt, is sent a message meanwhile
        await using var harness = new LifecycleHarness(StoppedThenCarriesOn(In(TimeSpan.FromSeconds(3))), settings: NoMargin);
        var limitedOne = await harness.CreateProjectAsync("first");
        await WaitForLimitedAsync(harness, limitedOne.Id);
        harness.UseScript(new FakeScript().EmitInit().AwaitStdin().EmitUser("Fix it", echo: true).EmitAssistant("Fixed.")
            .Sleep(50).EmitResult("fixed").AwaitStdin());
        var idle = await harness.CreateProjectAsync("second", prompt: null);
        await harness.WaitForLaunchAsync(idle.Id, _ => true);

        await harness.Projects.SendInputAsync(idle.Id, "Fix it");
        var held = await harness.Projects.GetStatusAsync(idle.Id);
        Assert.Equal("Fix it", held.RateLimited?.Held);
        Assert.Empty(harness.Launches(idle.Id)[0].Stdin);

        var sent = await harness.WaitForStdinAsync(idle.Id);
        Assert.Contains("Fix it", sent.Stdin[0]);
        await WaitForResultAsync(harness, idle.Id, "fixed");
    }
}
