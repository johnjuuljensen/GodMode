using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// A project recovered after a server restart can still call the MCP endpoint: the real server is
/// killed with its claude, a new one starts over the same roots, the project is resumed, and the
/// fake's permission prompt reaches the new server with the resumed launch's token and is answered.
/// Project tokens are not persisted: a restart leaves no process holding an old one, and every
/// launch is issued a fresh one.
/// </summary>
public class RestartBridgeTests
{
    private const string Profile = "restart";
    private const string Root = "lifecycle";

    /// <param name="portZero">The restarted server binds port 0, so only its bound address says where claude reaches it.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterARestart_TheResumedProjectsPermissionPromptIsAuthorisedAndAnswered(bool portZero)
    {
        var workDir = ServerProcess.CreateWorkDir("restart");
        var scriptPath = Path.Combine(workDir, "fake-claude.script");
        new FakeScript().EmitInit().AwaitStdin().AwaitStdin().Save(scriptPath);
        var rootConfig = Path.Combine(workDir, "roots", Root, ".godmode-root");
        Directory.CreateDirectory(rootConfig);
        File.WriteAllText(Path.Combine(rootConfig, "config.json"), JsonSerializer.Serialize(new
        {
            profileName = Profile,
            environment = new Dictionary<string, string>
            {
                [FakeClaudeEnvironment.Script] = scriptPath,
                [FakeClaudeEnvironment.Record] = "fake-claude.jsonl",
            },
        }));
        var fake = new Dictionary<string, string> { ["Claude__Executable"] = LifecycleHarness.FakeClaudePath };
        string? record = null;

        var first = ServerProcess.Start(workDir, $"http://127.0.0.1:{ServerProcess.GetFreePort()}", environment: fake);
        ServerProcess? second = null;
        try
        {
            string projectId;
            {
                var baseUrl = await first.WaitForListeningUrlAsync();
                using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
                await first.WaitForHealthyAsync(http);
                await using var client = new ServerHubClient(baseUrl);
                await client.StartAsync();
                var created = await client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.CreateProject), Profile, Root, null,
                    new Dictionary<string, JsonElement>
                    {
                        ["name"] = JsonSerializer.SerializeToElement("p1"),
                        ["prompt"] = JsonSerializer.SerializeToElement("Start"),
                    });
                projectId = created.Id;
                record = Path.Combine(workDir, "roots", Root, projectId.Split('/')[^1], "fake-claude.jsonl");
                Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(FakeRecording.Read(record) is [{ Stdin.Count: > 0 }])),
                    $"the first launch never got its prompt.\n{first.Output}");
            }

            // The server dies with its claude, as on a crash: nothing survives it
            first.Dispose();
            new FakeScript().EmitInit().AskPermission("Bash", new { command = "ls" }, "toolu_ls").EmitResult().AwaitStdin().Save(scriptPath);

            second = ServerProcess.Start(workDir, portZero ? "http://127.0.0.1:0" : $"http://127.0.0.1:{ServerProcess.GetFreePort()}",
                environment: fake);
            var restartedUrl = await second.WaitForListeningUrlAsync();
            using var restartedHttp = new HttpClient { BaseAddress = new Uri(restartedUrl), Timeout = TimeSpan.FromSeconds(10) };
            await second.WaitForHealthyAsync(restartedHttp);
            await using var restarted = new ServerHubClient(restartedUrl);
            await restarted.StartAsync();
            Assert.True(await LifecycleHarness.WaitForAsync(async () =>
                    (await restarted.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Any(p => p.Id == projectId)),
                $"the restarted server did not recover {projectId}.\n{second.Output}");

            await restarted.Hub.InvokeAsync(nameof(IProjectHub.ResumeProject), projectId);
            var asking = await restarted.WaitForAsync(projectId, s => s.PendingPermission != null, second);
            Assert.Equal("Bash: ls", asking.PendingPermission!.Summary);
            await restarted.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), projectId, asking.PendingPermission.RequestId,
                new PermissionDecision(true));

            Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(FakeRecording.Read(record).Count == 2
                    && FakeRecording.Read(record)[1].Permissions.Count == 1)),
                $"the resumed launch's permission call was not answered.\n{second.Output}");
            var resumed = FakeRecording.Read(record)[1];
            Assert.Equal(restartedUrl + McpEndpointUrl.Path, GodModeMcpEntry.Of(resumed).Url);
            using var answer = JsonDocument.Parse(resumed.Permissions[0]);
            Assert.Equal("allow", answer.RootElement.GetProperty("behavior").GetString());
            await restarted.WaitForAsync(projectId, s => s.State == ProjectState.Idle, second);
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
