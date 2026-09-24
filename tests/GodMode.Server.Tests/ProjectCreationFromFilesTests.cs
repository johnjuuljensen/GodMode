using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests;

/// <summary>
/// Configuration is edited as files on the host. A root laid out on disk, with a profile under
/// .profiles/, must still list its custom schema, run its create script, and hand the project the
/// three-level MCP merge (profile → root → action) — without any in-app editor having touched it.
/// </summary>
public class ProjectCreationFromFilesTests
{
    [Fact]
    public async Task ListProjectRoots_ReturnsTheActionsCustomSchema()
    {
        var workDir = ServerProcess.CreateWorkDir("create");
        try
        {
            WriteRootAndProfile(Path.Combine(workDir, "roots"));
            await using var services = BuildServices(workDir);
            var projects = services.GetRequiredService<IProjectManager>();

            var roots = await projects.ListProjectRootsAsync();

            var root = Assert.Single(roots, r => r.Name == "shipit");
            Assert.Equal("team", root.ProfileName);
            var action = Assert.Single(root.Actions!, a => a.Name == "issue");
            var properties = action.InputSchema!.Value.GetProperty("properties");
            Assert.Equal("Issue Number", properties.GetProperty("issueNumber").GetProperty("title").GetString());
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task CreateProject_RunsTheCreateScript_AndLaunchesWithTheMergedMcpConfig()
    {
        var workDir = ServerProcess.CreateWorkDir("create");
        try
        {
            var rootsDir = Path.Combine(workDir, "roots");
            WriteRootAndProfile(rootsDir);
            await using var services = BuildServices(workDir);
            var projects = services.GetRequiredService<IProjectManager>();
            var launcher = (RecordingProcessManager)services.GetRequiredService<IClaudeProcessManager>();

            var inputs = new Dictionary<string, JsonElement>
            {
                ["issueNumber"] = JsonSerializer.SerializeToElement("42"),
            };
            var status = await projects.CreateProjectAsync(new CreateProjectRequest("team", "shipit", inputs, "issue"));

            Assert.Equal("issue_42", status.Name);
            var marker = Path.Combine(rootsDir, "shipit", status.Id, "created-by-script.txt");
            Assert.True(File.Exists(marker), $"create script did not run: no {marker}");
            Assert.Equal("42", File.ReadAllText(marker).Trim());

            var launch = Assert.Single(launcher.Launches);
            using var mcp = JsonDocument.Parse(McpConfigOf(launch.Args));
            var servers = mcp.RootElement.GetProperty("mcpServers");
            Assert.Equal(
                ["action-over-root", "from-action", "from-profile", "from-root", "godmode-bridge", "root-over-profile"],
                servers.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("profile-cmd", servers.GetProperty("from-profile").GetProperty("command").GetString());
            Assert.Equal("root-cmd", servers.GetProperty("from-root").GetProperty("command").GetString());
            Assert.Equal("https://mcp.example.test/mcp", servers.GetProperty("from-action").GetProperty("url").GetString());
            Assert.Equal("root-wins", servers.GetProperty("root-over-profile").GetProperty("command").GetString());
            Assert.Equal("action-wins", servers.GetProperty("action-over-root").GetProperty("command").GetString());
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task CreateProject_NeverLogsAnMcpHeaderValue_AndKeepsTheConfigInTheProject()
    {
        const string headerVariable = "GODMODE_TEST_MCP_HEADER";
        var canary = "canary-" + Guid.NewGuid().ToString("N");
        var workDir = ServerProcess.CreateWorkDir("create");
        Environment.SetEnvironmentVariable(headerVariable, canary);
        try
        {
            var rootsDir = Path.Combine(workDir, "roots");
            WriteRootAndProfile(rootsDir);
            var logs = new CapturingLoggerProvider();
            await using var services = BuildServices(workDir, logs);
            var projects = services.GetRequiredService<IProjectManager>();
            var launcher = (RecordingProcessManager)services.GetRequiredService<IClaudeProcessManager>();

            var inputs = new Dictionary<string, JsonElement> { ["issueNumber"] = JsonSerializer.SerializeToElement("7") };
            var status = await projects.CreateProjectAsync(new CreateProjectRequest("team", "shipit", inputs, "issue"));

            var args = Assert.Single(launcher.Launches).Args!;
            var configPath = args[Array.IndexOf(args, "--mcp-config") + 1];
            Assert.Equal(Path.Combine(rootsDir, "shipit", status.Id, ".godmode", "mcp-config.json"), configPath);
            // The header did reach the config, so its absence from the log below means something
            Assert.Contains($"Bearer {canary}", File.ReadAllText(configPath));

            Assert.NotEmpty(logs.Lines);
            Assert.DoesNotContain(logs.Lines, l => l.Level >= LogLevel.Information && l.Text.Contains(canary));
        }
        finally
        {
            Environment.SetEnvironmentVariable(headerVariable, null);
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    /// <summary>
    /// One root, "shipit", in profile "team", with an "issue" action that has its own schema.json and
    /// create script. MCP servers are declared at every level, with one name overridden at each step.
    /// </summary>
    private static void WriteRootAndProfile(string rootsDir)
    {
        var profileMcp = Path.Combine(rootsDir, ".profiles", "team", "mcp");
        Directory.CreateDirectory(profileMcp);
        File.WriteAllText(Path.Combine(profileMcp, "from-profile.json"), """{ "command": "profile-cmd" }""");
        File.WriteAllText(Path.Combine(profileMcp, "root-over-profile.json"), """{ "command": "profile-loses" }""");

        var godModeRoot = Path.Combine(rootsDir, "shipit", ".godmode-root");
        Directory.CreateDirectory(Path.Combine(godModeRoot, "issue"));
        File.WriteAllText(Path.Combine(godModeRoot, "config.json"), """
            {
              "profileName": "team",
              "mcpServers": {
                "from-root": { "command": "root-cmd" },
                "root-over-profile": { "command": "root-wins" },
                "action-over-root": { "command": "root-loses" }
              }
            }
            """);
        File.WriteAllText(Path.Combine(godModeRoot, "config.issue.json"), """
            {
              "create": "issue/create",
              "nameTemplate": "issue_{issueNumber}",
              "promptTemplate": "Work on issue {issueNumber}",
              "mcpServers": {
                "from-action": { "url": "https://mcp.example.test/mcp", "headers": { "Authorization": "Bearer ${GODMODE_TEST_MCP_HEADER}" } },
                "action-over-root": { "command": "action-wins" }
              }
            }
            """);
        File.WriteAllText(Path.Combine(godModeRoot, "issue", "schema.json"), """
            {
              "type": "object",
              "properties": {
                "issueNumber": { "type": "string", "title": "Issue Number" }
              },
              "required": ["issueNumber"]
            }
            """);
        File.WriteAllText(Path.Combine(godModeRoot, "issue", "create.ps1"), """
            $ErrorActionPreference = 'Stop'
            Set-Content -Path (Join-Path $env:GODMODE_PROJECT_PATH 'created-by-script.txt') -Value $env:GODMODE_INPUT_ISSUE_NUMBER
            """);
    }

    private static string McpConfigOf(string[]? args)
    {
        Assert.NotNull(args);
        var index = Array.IndexOf(args!, "--mcp-config");
        Assert.True(index >= 0 && index + 1 < args!.Length, $"no --mcp-config in: {string.Join(' ', args!)}");
        return File.ReadAllText(args[index + 1]);
    }

    private static ServiceProvider BuildServices(string workDir, ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = Path.Combine(workDir, "roots"),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs == null) return;
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(logs);
        });
        services.AddSignalR();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IClaudeProcessManager, RecordingProcessManager>();
        services.AddSingleton<IStatusUpdater, StatusUpdater>();
        services.AddSingleton<ProjectLifecycle>();
        services.AddSingleton<IRootConfigReader, RootConfigReader>();
        services.AddSingleton<IScriptRunner, ScriptRunner>();
        services.AddSingleton<ProfileFileManager>();
        services.AddSingleton<IHostApplicationLifetime, ApplicationLifetime>();
        services.AddSingleton<IProjectManager, ProjectManager>();
        return services.BuildServiceProvider();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Lines);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private sealed class RecordingProcessManager : IClaudeProcessManager
    {
        public List<(Dictionary<string, string>? Env, string[]? Args)> Launches { get; } = [];

        public Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null) => Record(extraEnvironment, extraArgs);

        public Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null) => Record(extraEnvironment, extraArgs);

        private Task<int> Record(Dictionary<string, string>? env, string[]? args)
        {
            Launches.Add((env == null ? null : new Dictionary<string, string>(env), args));
            return Task.FromResult(4242 + Launches.Count);
        }

        public Task SendInputAsync(ProjectInfo project, string input) => Task.CompletedTask;
        public Task StopProcessAsync(ProjectInfo project) => Task.CompletedTask;
        public bool IsProcessRunning(int processId) => false;
    }
}
