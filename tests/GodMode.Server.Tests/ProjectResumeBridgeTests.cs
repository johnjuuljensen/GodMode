using System.Text.Json;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Server.Tests;

/// <summary>
/// Project tokens live only in memory. A project recovered after a server restart and then
/// resumed must still get a working MCP bridge: the bridge in its MCP config, and a token
/// the server accepts.
/// </summary>
public class ProjectResumeBridgeTests
{
    [Fact]
    public async Task ResumeAfterRestart_LaunchesTheBridgeWithATokenTheServerAccepts()
    {
        var workDir = ServerProcess.CreateWorkDir("resume");
        try
        {
            var rootPath = Path.Combine(workDir, "work");
            var projectPath = Path.Combine(rootPath, "proj1");
            WriteStoppedProject(projectPath, "proj1");

            // A fresh ProjectManager is what a restarted server has: nothing in memory.
            await using var services = BuildServices(workDir, rootPath);
            var projects = services.GetRequiredService<IProjectManager>();
            var launcher = (RecordingProcessManager)services.GetRequiredService<IClaudeProcessManager>();

            await projects.RecoverProjectsAsync();
            await projects.ResumeProjectAsync("proj1");

            var launch = Assert.Single(launcher.Launches);
            Assert.NotNull(launch.Env);
            Assert.Equal("proj1", launch.Env!["GODMODE_PROJECT_ID"]);
            Assert.StartsWith("http://localhost:", launch.Env["GODMODE_SERVER_URL"]);
            var token = launch.Env["GODMODE_PROJECT_TOKEN"];
            Assert.NotNull(projects.ValidateProjectToken("proj1", token));

            Assert.Contains("godmode-bridge", McpConfigOf(launch.Args));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task EachLaunchIssuesAFreshToken_AndRetiresThePreviousOne()
    {
        var workDir = ServerProcess.CreateWorkDir("resume");
        try
        {
            var rootPath = Path.Combine(workDir, "work");
            WriteStoppedProject(Path.Combine(rootPath, "proj1"), "proj1");

            await using var services = BuildServices(workDir, rootPath);
            var projects = services.GetRequiredService<IProjectManager>();
            var launcher = (RecordingProcessManager)services.GetRequiredService<IClaudeProcessManager>();

            await projects.RecoverProjectsAsync();
            await projects.ResumeProjectAsync("proj1");
            launcher.Running = false; // the first process exits
            await projects.ResumeProjectAsync("proj1");

            Assert.Equal(2, launcher.Launches.Count);
            var first = launcher.Launches[0].Env!["GODMODE_PROJECT_TOKEN"];
            var second = launcher.Launches[1].Env!["GODMODE_PROJECT_TOKEN"];
            Assert.NotEqual(first, second);
            Assert.Null(projects.ValidateProjectToken("proj1", first));
            Assert.NotNull(projects.ValidateProjectToken("proj1", second));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    private static string McpConfigOf(string[]? args)
    {
        Assert.NotNull(args);
        var index = Array.IndexOf(args!, "--mcp-config");
        Assert.True(index >= 0 && index + 1 < args!.Length, $"no --mcp-config in: {string.Join(' ', args!)}");
        // --mcp-config takes a file path; the server writes the JSON to a temp file
        return File.ReadAllText(args[index + 1]);
    }

    private static void WriteStoppedProject(string projectPath, string id)
    {
        var godMode = Path.Combine(projectPath, ".godmode");
        Directory.CreateDirectory(godMode);
        var now = DateTime.UtcNow;
        var status = new ProjectStatus(id, id, ProjectState.Stopped, now, now, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        File.WriteAllText(Path.Combine(godMode, "status.json"), JsonSerializer.Serialize(status, JsonDefaults.Options));
        File.WriteAllText(Path.Combine(godMode, "session-id"), Guid.NewGuid().ToString());
    }

    private static ServiceProvider BuildServices(string workDir, string rootPath)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = Path.Combine(workDir, "roots"),
            ["ProjectRoots:work"] = rootPath,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IClaudeProcessManager, RecordingProcessManager>();
        services.AddSingleton<IStatusUpdater, StatusUpdater>();
        services.AddSingleton<IRootConfigReader, RootConfigReader>();
        services.AddSingleton<IScriptRunner, ScriptRunner>();
        services.AddSingleton<ProfileFileManager>();
        services.AddSingleton<IProjectManager, ProjectManager>();
        return services.BuildServiceProvider();
    }

    private sealed class RecordingProcessManager : IClaudeProcessManager
    {
        public List<(Dictionary<string, string>? Env, string[]? Args)> Launches { get; } = [];
        public bool Running { get; set; }

        public event OutputReceivedHandler? OnOutputReceived { add { } remove { } }
        public event ProcessExitedHandler? OnProcessExited { add { } remove { } }

        public Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null) => Record(extraEnvironment, extraArgs);

        public Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null) => Record(extraEnvironment, extraArgs);

        private Task<int> Record(Dictionary<string, string>? env, string[]? args)
        {
            Launches.Add((env == null ? null : new Dictionary<string, string>(env), args));
            Running = true;
            return Task.FromResult(4242 + Launches.Count);
        }

        public Task SendInputAsync(ProjectInfo project, string input) => Task.CompletedTask;
        public Task StopProcessAsync(ProjectInfo project) => Task.CompletedTask;
        public bool IsProcessRunning(int processId) => Running;
    }
}
