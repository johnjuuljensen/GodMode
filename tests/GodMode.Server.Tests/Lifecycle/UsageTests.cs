using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Issue #600: every <c>rate_limit_event</c>'s windows are its account's usage reading, which the roots and profiles on the
/// account show, and a root whose account is at or above its usage ceiling starts and resumes no session, saying why. The
/// harness's sessions share one account: none sets <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
public class UsageTests
{
    private static DateTimeOffset In(TimeSpan time) => DateTimeOffset.UtcNow + time;

    /// <summary>Each turn reports its account at 80% of its five-hour window (an <c>allowed_warning</c>), resetting at <paramref name="resetsAt"/>.</summary>
    private static FakeScript Reports(DateTimeOffset resetsAt) =>
        new FakeScript().EmitInit().AwaitStdin().EmitUser("Say hello", echo: true)
            .EmitRateLimit(resetsAt, status: "allowed_warning").EmitResult("done").AwaitStdin();

    /// <summary>The root's config.json with its <c>usageCeiling</c> set to <paramref name="fiveHour"/>.</summary>
    private static Dictionary<string, object> Ceiling(double fiveHour) => new() { ["usageCeiling"] = new { fiveHour } };

    private static void SetCeiling(LifecycleHarness harness, double fiveHour)
    {
        var path = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        config["usageCeiling"] = new JsonObject { ["fiveHour"] = fiveHour };
        File.WriteAllText(path, config.ToJsonString());
    }

    private static async Task<AccountUsage> WaitForReadingAsync(LifecycleHarness harness)
    {
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Projects.GetUsage().Length > 0), null,
            () => "no usage reading came");
        return Assert.Single(harness.Projects.GetUsage());
    }

    [Fact]
    public async Task AReading_ReachesTheRootAndProfileOnItsAccount_AndIsPushed()
    {
        var resetsAt = In(TimeSpan.FromHours(3));
        await using var harness = new LifecycleHarness(Reports(resetsAt));
        Assert.Empty(harness.Projects.GetUsage());
        await harness.CreateProjectAsync();

        var usage = await WaitForReadingAsync(harness);
        Assert.Equal(0.8, usage.FiveHour!.Utilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAt.ToUnixTimeSeconds()).UtcDateTime, usage.FiveHour.ResetsAt);
        Assert.Null(usage.SevenDay);
        Assert.Equal(usage.Account, Assert.Single(await harness.Projects.ListProjectRootsAsync(), r => r.Name == LifecycleHarness.RootName).Account);
        Assert.Equal(usage.Account, Assert.Single(await harness.Projects.ListProfilesAsync(), p => p.Name == LifecycleHarness.ProfileName).Account);
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.Hub.Pushes.Any(p => p.Method == nameof(IProjectHubClient.UsageChanged) && p.Usage?.Single() == usage)),
            null, () => "the reading was not pushed");
    }

    [Fact]
    public async Task ARootAboveItsCeiling_RefusesACreateAndAResume_SayingWhy_AndBelowItDoesNot()
    {
        await using var harness = new LifecycleHarness(Reports(In(TimeSpan.FromHours(3))), rootConfig: Ceiling(0.7));
        // No reading yet: nothing holds
        var first = await harness.CreateProjectAsync("first");
        await WaitForReadingAsync(harness);
        Assert.Equal(new UsageCeiling(FiveHour: 0.7), Assert.Single(await harness.Projects.ListProjectRootsAsync()).UsageCeiling);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateProjectAsync("second"));
        Assert.Contains("holds its work", refused.Message);
        Assert.Contains("80% of its five-hour limit, over its ceiling of 70%", refused.Message);

        // A stopped session is not resumed, by the app's resume or a reply; a running one is left alone
        await harness.Projects.StopProjectAsync(first.Id);
        Assert.Contains("holds its work", (await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.ResumeProjectAsync(first.Id))).Message);
        Assert.Contains("holds its work", (await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.ReplyAndResumeAsync(first.Id, "go on"))).Message);
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(first.Id)).State);

        // Above the reading, the ceiling holds nothing
        SetCeiling(harness, 0.9);
        await harness.CreateProjectAsync("third");
        await harness.Projects.ResumeProjectAsync(first.Id);
    }

    [Fact]
    public async Task AWindowWhoseResetHasPassed_HoldsNothing()
    {
        await using var harness = new LifecycleHarness(Reports(In(TimeSpan.FromSeconds(-30))), rootConfig: Ceiling(0.7));
        await harness.CreateProjectAsync("first");
        await WaitForReadingAsync(harness);

        await harness.CreateProjectAsync("second");
    }

    [Fact]
    public async Task AProfilesCeiling_HoldsItsRoots_AndTheFleetsStartSessionIsRefused()
    {
        var instanceConfig = $$"""{ "Profiles": { "{{FleetRun.Profile}}": { "UsageCeiling": { "FiveHour": 70 } } } }""";
        await using var run = await FleetRun.StartAsync(Reports(In(TimeSpan.FromHours(3))), instanceConfig);
        await run.CreateOverHubAsync("first");
        await LifecycleHarness.WaitUntilAsync(
            async () => (await run.Client.Hub.InvokeAsync<AccountUsage[]>(nameof(IProjectHub.GetUsage))).Length > 0, null,
            () => $"no usage reading came\n{run.Server.Output}");

        await using var fleet = await run.ConnectFleetAsync();
        var refused = await run.RefusedAsync(fleet, "start_session", FleetRun.StartArguments("worker", FleetRun.WorkAction));
        Assert.Contains($"Root '{FleetRun.RootName}' holds its work", refused);
        // Another profile's root on the same account, with no ceiling, starts sessions
        await run.CreateOverHubAsync("other", FleetRun.WorkAction, FleetRun.OtherProfile, FleetRun.OtherRoot);
    }
}
