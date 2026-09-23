using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Secrets the server holds stay out of the Claude processes it launches, through the real
/// ProjectManager and ClaudeProcessManager against the fake claude: the process starts from the
/// allowlist plus config, and the MCP config file lives only as long as the process.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public class SecretsHygieneTests
{
    [Fact]
    public async Task ServerCanary_IsNotInTheLaunchedProcessesEnvironment()
    {
        const string canaryName = "GODMODE_TEST_SECRET";
        var canary = "canary-" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(canaryName);
        Environment.SetEnvironmentVariable(canaryName, canary);
        try
        {
            await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
            var created = await harness.CreateProjectAsync();

            var launch = await harness.WaitForStdinAsync(created.Id);

            Assert.DoesNotContain(launch.Environment, v => v.Key.Equals(canaryName, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(launch.Environment, v => v.Value == canary);
            // Everything the process got is on the allowlist, from the root's config, or set by the server for the launch
            string[] configured = [FakeClaudeEnvironment.Script, FakeClaudeEnvironment.Record,
                "GODMODE_PROJECT_ID", "GODMODE_PROJECT_TOKEN", "GODMODE_SERVER_URL"];
            Assert.All(launch.Environment.Keys, name => Assert.True(
                ChildEnvironment.IsAllowed(name) || configured.Contains(name, StringComparer.OrdinalIgnoreCase),
                $"{name} is neither allowlisted nor configured"));
            Assert.Contains(launch.Environment.Keys, name => name.Equals("PATH", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryName, previous);
        }
    }

    [Fact]
    public async Task McpConfigFile_ExistsWhileTheProcessRuns_AndIsGoneAfterItExits()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().AwaitStdin().Exit(0));
        var created = await harness.CreateProjectAsync();
        var configPath = McpConfigFile.PathFor(harness.ProjectPath(created.Id));

        var launch = await harness.WaitForStdinAsync(created.Id);
        Assert.Equal(configPath, launch.ArgValue("--mcp-config"));
        Assert.True(File.Exists(configPath), $"{configPath} should exist while the process runs");

        await harness.Projects.SendInputAsync(created.Id, "That is all");
        await harness.WaitForLaunchAsync(created.Id, l => l.ExitCode == 0);

        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(!File.Exists(configPath))),
            $"{configPath} is still there after the process exited.\n{harness.Describe(created.Id)}");
    }
}

/// <summary>Tests that set variables on the test process's own environment, which every test shares.</summary>
[CollectionDefinition(Name)]
public sealed class ServerEnvironmentCollection
{
    public const string Name = "Server process environment";
}
