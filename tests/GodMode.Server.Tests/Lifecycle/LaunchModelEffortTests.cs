using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A root or an action names claude's model (<c>--model</c>) and effort (<c>--effort</c>); a create's
/// <c>model</c> and <c>effort</c> inputs override them. Both are kept with the session at create and
/// passed to each of its launches, whatever its root's config says by then. An effort claude does not
/// have is refused, in a config file or a create alike, before anything is created.
/// </summary>
public class LaunchModelEffortTests
{
    private const string Model = "--model";
    private const string Effort = "--effort";

    private static string Overlay(LifecycleHarness harness) => Path.Combine(harness.RootPath, ".godmode-root", "config.work.json");

    private static void AssertNothingIsCreated(LifecycleHarness harness) =>
        Assert.Equal([".godmode-root", "logs"], Directory.GetFileSystemEntries(harness.RootPath).Select(Path.GetFileName).Order());

    private static async Task<FakeLaunch> StopAndResumeAsync(LifecycleHarness harness, string projectId)
    {
        await harness.Projects.StopProjectAsync(projectId);
        await harness.WaitForStateAsync(projectId, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(projectId);
        return await harness.WaitForLaunchAsync(projectId, _ => true, index: 1);
    }

    [Fact]
    public async Task ModelAndEffortInAnOverlay_ArePassedOnCreateAndResume_EvenAfterTheRootConfigHasChanged()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Overlay(harness), """{ "model": "fable", "effort": "xhigh" }""");

        var created = await harness.CreateProjectAsync();
        var create = await harness.WaitForStdinAsync(created.Id);
        File.WriteAllText(Overlay(harness), """{ "model": "haiku", "effort": "low" }""");
        var resume = await StopAndResumeAsync(harness, created.Id);

        Assert.Equal(("fable", "xhigh"), (create.ArgValue(Model), create.ArgValue(Effort)));
        Assert.Equal(("fable", "xhigh"), (resume.ArgValue(Model), resume.ArgValue(Effort)));
        Assert.Equal("xhigh", (await harness.Projects.GetStatusAsync(created.Id)).Effort);
    }

    /// <summary>The create form's choice wins over the action's, its case as claude spells it.</summary>
    [Fact]
    public async Task EffortInput_OverridesTheAction()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Overlay(harness), """{ "effort": "low" }""");

        var created = await harness.CreateProjectAsync(inputs: new Dictionary<string, object> { ["effort"] = "MAX" });
        var create = await harness.WaitForStdinAsync(created.Id);
        var resume = await StopAndResumeAsync(harness, created.Id);

        Assert.Equal("max", create.ArgValue(Effort));
        Assert.Equal("max", resume.ArgValue(Effort));
    }

    /// <summary>The form's empty choice is claude's own default, over the action's level, on resume too.</summary>
    [Fact]
    public async Task EmptyEffortInput_PassesNoEffort_OverTheAction()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Overlay(harness), """{ "effort": "high" }""");

        var created = await harness.CreateProjectAsync(inputs: new Dictionary<string, object> { ["effort"] = "" });
        var create = await harness.WaitForStdinAsync(created.Id);
        var resume = await StopAndResumeAsync(harness, created.Id);

        Assert.DoesNotContain(Effort, create.Argv);
        Assert.DoesNotContain(Effort, resume.Argv);
    }

    [Fact]
    public async Task NoEffortAnywhere_PassesNoEffort()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());

        var created = await harness.CreateProjectAsync();

        Assert.DoesNotContain(Effort, (await harness.WaitForStdinAsync(created.Id)).Argv);
    }

    [Fact]
    public async Task InvalidEffortInput_FailsTheCreate_AndNothingIsCreated()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CreateProjectAsync(inputs: new Dictionary<string, object> { ["effort"] = "turbo" }));

        Assert.Contains("effort 'turbo' is not one of low, medium, high, xhigh, max", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        AssertNothingIsCreated(harness);
    }

    [Fact]
    public async Task InvalidEffortInConfig_FailsTheCreate_AndNothingIsCreated()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Overlay(harness), """{ "effort": "turbo" }""");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync());

        Assert.Contains("config.work.json: effort 'turbo' is not one of", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        AssertNothingIsCreated(harness);
    }

    /// <summary>status.json is the session's to write: a level it does not name is dropped at launch, said once.</summary>
    [Fact]
    public async Task KeptEffortThatIsNoLevel_LaunchesWithoutEffort()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        File.WriteAllText(Overlay(harness), """{ "effort": "high" }""");
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        var statusPath = Path.Combine(harness.StatePath(created.Id), "status.json");
        File.WriteAllText(statusPath, File.ReadAllText(statusPath).Replace("\"high\"", "\"--dangerously-skip-permissions\""));
        await harness.RestartAsync();

        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.DoesNotContain(Effort, resume.Argv);
        Assert.DoesNotContain("--dangerously-skip-permissions", resume.Argv);
        Assert.Equal(1, harness.Warnings.Count(line => line.Contains("it launches without --effort")));
    }

    /// <summary>Each action tells the create form its model and effort, the overlay's over the base's.</summary>
    [Fact]
    public async Task ListProjectRoots_GivesEachActionsModelAndEffort()
    {
        await using var harness = new LifecycleHarness(new FakeScript(),
            rootConfig: new Dictionary<string, object> { ["model"] = "opus", ["effort"] = "medium" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.base.json"), "{}");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.deep.json"), """{ "model": "fable", "effort": "max" }""");

        var root = Assert.Single(await harness.Projects.ListProjectRootsAsync(), r => r.Name == LifecycleHarness.RootName);

        Assert.Equal(
            [("base", "opus", "medium"), ("deep", "fable", "max")],
            root.Actions!.Select(a => (a.Name, a.Model, a.Effort)).OrderBy(a => a.Name));
    }
}
