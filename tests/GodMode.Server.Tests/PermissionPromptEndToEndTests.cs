using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Server.Tests;

/// <summary>
/// A permission prompt from end to end, against the real server process: the fake claude calls
/// the permission_prompt tool on GodMode's MCP endpoint as claude does, an MCP client with the url
/// and headers of its --mcp-config, a SignalR client sees the request in the status and answers it
/// with RespondToPermission or AnswerQuestion, and the fake receives what claude would.
/// </summary>
public class PermissionPromptEndToEndTests
{
    private const string Profile = "e2e";
    private const string Root = "lifecycle";
    private const string Question = "Which color do you prefer?";

    [Fact]
    public async Task FakeAsksThroughTheMcpEndpoint_ClientAnswers_FakeReceivesTheDecision()
    {
        var workDir = ServerProcess.CreateWorkDir("permission");
        var scriptPath = Path.Combine(workDir, "fake-claude.script");
        new FakeScript()
            .EmitInit()
            .AwaitStdin()
            .AskPermission("Bash", new { command = "git push origin feature/12-x", description = "Push the branch" }, "toolu_push")
            .AskPermission("Bash", new { command = "rm -rf build" }, "toolu_rm")
            .AskPermission("AskUserQuestion", new
            {
                questions = new[]
                {
                    new { question = Question, header = "Color", options = new[] { new { label = "Red", description = "Warm" }, new { label = "Blue", description = "Cool" } }, multiSelect = false },
                },
            }, "toolu_ask")
            .EmitAssistant("All done.")
            .EmitResult()
            .Save(scriptPath);
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

        var port = ServerProcess.GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var server = ServerProcess.Start(workDir, baseUrl,
            environment: new Dictionary<string, string> { ["Claude__Executable"] = LifecycleHarness.FakeClaudePath });
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            await server.WaitForHealthyAsync(http);

            // ── Allowed with an edited input; the request outlives the client that saw it ──
            await using var first = new ServerHubClient(baseUrl);
            await first.StartAsync();
            var created = await first.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.CreateProject), Profile, Root, null,
                new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("p1"),
                    ["prompt"] = JsonSerializer.SerializeToElement("Push it"),
                });
            var push = await first.WaitForAsync(created.Id, s => s.PendingPermission != null, server);
            Assert.Equal(ProjectState.WaitingPermission, push.State);
            Assert.Equal("Bash", push.PendingPermission!.ToolName);
            Assert.Equal("Bash: git push origin feature/12-x", push.PendingPermission.Summary);
            // The push carries the summary; the whole of what runs is fetched (#234)
            var detail = await first.Hub.InvokeAsync<PermissionDetail>(nameof(IProjectHub.GetPermissionDetail), created.Id, push.PendingPermission.RequestId);
            Assert.Equal(new PermissionDetail(push.PendingPermission.RequestId, "git push origin feature/12-x", false), detail);
            await first.DisposeAsync();

            await using var second = new ServerHubClient(baseUrl);
            await second.StartAsync();
            var listed = Assert.Single(await second.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)));
            Assert.Equal(ProjectState.WaitingPermission, listed.State);
            Assert.Equal(push.PendingPermission.RequestId, listed.PendingPermission?.RequestId);
            var edited = JsonSerializer.SerializeToElement(new { command = "git push --dry-run origin feature/12-x", description = "Push the branch" });
            await second.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), created.Id, push.PendingPermission.RequestId,
                new PermissionDecision(true, UpdatedInput: edited));
            // Only one answer counts: a second, as from another client, is an error (#234)
            var late = await Assert.ThrowsAsync<HubException>(() => second.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission),
                created.Id, push.PendingPermission.RequestId, new PermissionDecision(false)));
            Assert.Contains(push.PendingPermission.RequestId, late.Message);

            // ── Denied with a message ──
            var remove = await second.WaitForAsync(created.Id, s => s.PendingPermission is { } p && p.RequestId != push.PendingPermission.RequestId, server);
            Assert.Equal("Bash: rm -rf build", remove.PendingPermission!.Summary);
            await second.Hub.InvokeAsync(nameof(IProjectHub.RespondToPermission), created.Id, remove.PendingPermission.RequestId,
                new PermissionDecision(false, "Not in this session"));

            // ── AskUserQuestion is a question, not a tool call to allow ──
            var asked = await second.WaitForAsync(created.Id, s => s.PendingQuestion != null, server);
            Assert.Equal(ProjectState.WaitingInput, asked.State);
            Assert.Null(asked.PendingPermission);
            Assert.Equal(Question, asked.CurrentQuestion);
            var question = Assert.Single(asked.PendingQuestion!.Questions);
            Assert.Equal(Question, question.Question);
            Assert.Equal("Color", question.Header);
            Assert.Equal(["Red", "Blue"], question.Options.Select(o => o.Label));
            await second.Hub.InvokeAsync(nameof(IProjectHub.AnswerQuestion), created.Id, asked.PendingQuestion.RequestId,
                new Dictionary<string, string> { [Question] = "Blue" });

            var idle = await second.WaitForAsync(created.Id, s => s.State == ProjectState.Idle, server);
            Assert.Null(idle.PendingPermission);
            Assert.Null(idle.PendingQuestion);

            // What the fake got back is what the tool hands claude
            var folder = created.Id.Split('/')[^1];
            var launch = Assert.Single(FakeRecording.Read(Path.Combine(workDir, "roots", Root, folder, "fake-claude.jsonl")));
            Assert.Equal(3, launch.Permissions.Count);

            // The one tool, with the flat arguments claude calls it with
            using var tools = JsonDocument.Parse(launch.Tools ?? throw new InvalidOperationException("the fake listed no tools"));
            var tool = Assert.Single(tools.RootElement.EnumerateArray());
            Assert.Equal("permission_prompt", tool.GetProperty("name").GetString());
            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.Equal("string", schema.GetProperty("properties").GetProperty("tool_name").GetProperty("type").GetString());
            Assert.Equal("object", schema.GetProperty("properties").GetProperty("input").GetProperty("type").GetString());
            Assert.True(schema.GetProperty("properties").TryGetProperty("tool_use_id", out _));
            Assert.Equal(["tool_name", "input"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));

            using var allowed = JsonDocument.Parse(launch.Permissions[0]);
            Assert.Equal("allow", allowed.RootElement.GetProperty("behavior").GetString());
            Assert.Equal("git push --dry-run origin feature/12-x", allowed.RootElement.GetProperty("updatedInput").GetProperty("command").GetString());
            Assert.False(allowed.RootElement.TryGetProperty("message", out _));

            using var denied = JsonDocument.Parse(launch.Permissions[1]);
            Assert.Equal("deny", denied.RootElement.GetProperty("behavior").GetString());
            Assert.Equal("Not in this session", denied.RootElement.GetProperty("message").GetString());

            using var answered = JsonDocument.Parse(launch.Permissions[2]);
            Assert.Equal("allow", answered.RootElement.GetProperty("behavior").GetString());
            var input = answered.RootElement.GetProperty("updatedInput");
            Assert.Equal("Blue", input.GetProperty("answers").GetProperty(Question).GetString());
            Assert.Equal(Question, input.GetProperty("questions")[0].GetProperty("question").GetString());

            // claude is launched to ask through the MCP endpoint
            Assert.Equal(ProjectManager.PermissionPromptTool, launch.ArgValue("--permission-prompt-tool"));
        }
        finally
        {
            server.Dispose();
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
