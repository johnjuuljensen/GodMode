using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Secrets the server holds, its own API key among them, stay out of the processes it launches,
/// through the real ProjectManager, ClaudeProcessManager and ScriptRunner: the fake claude and a
/// root's scripts start from their allowlist plus config, and the MCP config file lives only as
/// long as the process.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public class SecretsHygieneTests
{
    [Fact]
    public async Task ServerCanaries_AreNotInTheLaunchedProcessesEnvironment()
    {
        using var canaries = new ServerCanaries();
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();

        var launch = await harness.WaitForStdinAsync(created.Id);

        canaries.AssertAbsent(launch.Environment);
        // Everything the process got is on the allowlist or from the root's config: the server sets none
        string[] configured = [FakeClaudeEnvironment.Script, FakeClaudeEnvironment.Record];
        Assert.All(launch.Environment.Keys, name => Assert.True(
            ChildEnvironment.Claude.IsAllowed(name) || configured.Contains(name, StringComparer.OrdinalIgnoreCase),
            $"{name} is neither allowlisted nor configured"));
        Assert.Contains(launch.Environment.Keys, name => name.Equals("PATH", StringComparison.OrdinalIgnoreCase));
        // The project token is only in the MCP config file
        var token = GodModeMcpEntry.Of(launch).Token;
        Assert.DoesNotContain(launch.Environment.Values, value => value.Contains(token));
    }

    /// <summary>
    /// A root's scripts run with the essentials and their config, as claude does: the status script
    /// at the turn's end, in a folder the session controls, and the prepare script before the project
    /// exists. Each writes the environment it got.
    /// </summary>
    [Theory]
    [InlineData("status")]
    [InlineData("prepare")]
    public async Task ServerCanaries_AreNotInARootScriptsEnvironment(string script)
    {
        using var canaries = new ServerCanaries();
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitResult("Done."),
            rootConfig: new Dictionary<string, object> { [script] = $"scripts/{script}" });
        var dump = Path.Combine(harness.WorkDir, $"{script}-env.txt");
        var scripts = Path.Combine(harness.RootPath, ".godmode-root", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, $"{script}.ps1"), $$"""
            $ErrorActionPreference = 'Stop'
            Get-ChildItem env: | ForEach-Object { "$($_.Name)=$($_.Value)" } | Set-Content -Path '{{dump}}'
            {{(script == "status" ? "'{}'" : "")}}
            """);

        var created = await harness.CreateProjectAsync();

        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(File.Exists(dump) && new FileInfo(dump).Length > 0), null,
            () => $"the {script} script did not run.\n{harness.Describe(created.Id)}");
        var environment = (await ReadWhenWrittenAsync(dump))
            .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.OrdinalIgnoreCase);

        canaries.AssertAbsent(environment);
        // What it needs to run, and what the server gives every script
        Assert.Contains("PATH", environment.Keys);
        Assert.Equal(harness.RootPath, environment["GODMODE_ROOT_PATH"]);
        // The root's configured environment
        Assert.Contains(FakeClaudeEnvironment.Script, environment.Keys);
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

    /// <summary>The script may still be writing it; a locked or partial read is retried.</summary>
    private static async Task<string[]> ReadWhenWrittenAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(path);
                if (lines.Any(line => line.StartsWith("GODMODE_ROOT_PATH=", StringComparison.Ordinal)) || attempt >= 50) return lines;
            }
            catch (IOException) when (attempt < 50) { }
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Secrets on the server process's own environment for the length of a test: the server's API key
    /// (<c>Authentication__ApiKey</c>, as an operator sets it) and an arbitrary one.
    /// </summary>
    private sealed class ServerCanaries : IDisposable
    {
        private readonly Dictionary<string, (string Canary, string? Previous)> _set = new()
        {
            ["Authentication__ApiKey"] = ("key-canary-" + Guid.NewGuid().ToString("N"), null),
            ["GODMODE_TEST_SECRET"] = ("canary-" + Guid.NewGuid().ToString("N"), null),
        };

        public ServerCanaries()
        {
            foreach (var name in _set.Keys.ToList())
            {
                _set[name] = (_set[name].Canary, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, _set[name].Canary);
            }
        }

        public void AssertAbsent(IReadOnlyDictionary<string, string> environment)
        {
            foreach (var (name, (canary, _)) in _set)
            {
                Assert.DoesNotContain(environment, v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(environment.Values, value => value.Contains(canary));
            }
        }

        public void Dispose()
        {
            foreach (var (name, (_, previous)) in _set)
                Environment.SetEnvironmentVariable(name, previous);
        }
    }
}

/// <summary>Tests that set variables on the test process's own environment, which every test shares.</summary>
[CollectionDefinition(Name)]
public sealed class ServerEnvironmentCollection
{
    public const string Name = "Server process environment";
}
