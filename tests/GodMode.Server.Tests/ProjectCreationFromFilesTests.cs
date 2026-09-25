using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using System.Text.Json;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Server.Tests;

/// <summary>
/// Configuration is edited as files on the host. A root laid out on disk, with a profile under
/// .profiles/, must still list its custom schema and run its create script, without any in-app
/// editor having touched it.
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
    public async Task CreateProject_RunsTheCreateScript_AndLaunchesClaude()
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
            Assert.Equal("team/shipit/issue_42", status.Id);
            var marker = Path.Combine(rootsDir, "shipit", "issue_42", "created-by-script.txt");
            Assert.True(File.Exists(marker), $"create script did not run: no {marker}");
            Assert.Equal("42", File.ReadAllText(marker).Trim());
            Assert.Single(launcher.Launches);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    /// <summary>
    /// One root, "shipit", in profile "team" (a profile directory with a description), with an
    /// "issue" action that has its own schema.json and create script.
    /// </summary>
    private static void WriteRootAndProfile(string rootsDir)
    {
        var profileDir = Path.Combine(rootsDir, ".profiles", "team");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "profile.json"), """{ "description": "The team's roots" }""");

        var godModeRoot = Path.Combine(rootsDir, "shipit", ".godmode-root");
        Directory.CreateDirectory(Path.Combine(godModeRoot, "issue"));
        File.WriteAllText(Path.Combine(godModeRoot, "config.json"), """
            {
              "profileName": "team"
            }
            """);
        File.WriteAllText(Path.Combine(godModeRoot, "config.issue.json"), """
            {
              "create": "issue/create",
              "nameTemplate": "issue_{issueNumber}",
              "promptTemplate": "Work on issue {issueNumber}"
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

    private static ServiceProvider BuildServices(string workDir)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = Path.Combine(workDir, "roots"),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
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
