using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A project's <c>.godmode/</c> holds its MCP config, and the project token in it, for as long as
/// claude runs: its <c>.gitignore</c> keeps all of it out of a session's commits. Every launch makes
/// sure of that file, whoever made the folder.
/// </summary>
public class GitIgnoreTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>A create script whose checkout already has a <c>.godmode/</c>, and no <c>.gitignore</c> in it.</summary>
    [Fact]
    public async Task CheckoutThatHasGodModeWithoutAGitIgnore_GetsOne_AtLaunch()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "checkout.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "checkout.ps1"),
            "New-Item -ItemType Directory -Force (Join-Path $env:GODMODE_PROJECT_PATH '.godmode') | Out-Null");

        var created = await harness.CreateProjectAsync();

        var launch = await harness.WaitForStdinAsync(created.Id);
        AssertIgnoresEverything(harness.ProjectPath(created.Id));
        Assert.StartsWith(Path.Combine(harness.ProjectPath(created.Id), ".godmode"), launch.ArgValue("--mcp-config"));
    }

    /// <summary>One the session deleted (or a checkout replaced) is back before the resumed claude gets its token.</summary>
    [Fact]
    public async Task GitIgnoreThatIsGone_IsBack_WhenTheProjectIsResumed()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        var gitIgnore = Path.Combine(harness.ProjectPath(created.Id), ".godmode", ".gitignore");
        File.Delete(gitIgnore);

        await harness.Projects.ResumeProjectAsync(created.Id).WaitAsync(LifecycleHarness.DefaultTimeout);

        await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        AssertIgnoresEverything(harness.ProjectPath(created.Id));
    }

    /// <summary>A <c>.gitignore</c> of the checkout's own that ignores less gets the rule added, and keeps its own lines.</summary>
    [Fact]
    public async Task GitIgnoreThatDoesNotIgnoreEverything_GetsTheRule_AndKeepsItsLines()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "checkout.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "checkout.ps1"), """
            $godMode = Join-Path $env:GODMODE_PROJECT_PATH '.godmode'
            New-Item -ItemType Directory -Force $godMode | Out-Null
            Set-Content -Path (Join-Path $godMode '.gitignore') -Value 'status.json' -NoNewline
            """);

        var created = await harness.CreateProjectAsync();

        await harness.WaitForStdinAsync(created.Id);
        var lines = AssertIgnoresEverything(harness.ProjectPath(created.Id));
        Assert.Contains("status.json", lines);
    }

    private static string[] AssertIgnoresEverything(string projectPath)
    {
        var path = Path.Combine(projectPath, ".godmode", ".gitignore");
        Assert.True(File.Exists(path), $"{path} should exist");
        var lines = File.ReadAllLines(path).Select(line => line.Trim()).ToArray();
        Assert.Contains("*", lines);
        return lines;
    }
}
