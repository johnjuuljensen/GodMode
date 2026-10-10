using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// The trash over the real hub, as the app uses it (#325): a real server process, a SignalR client with
/// the server's key. DeleteProject says whether it trashed the session; RestoreProject brings it back
/// under the same ID, pushed to the client as ProjectCreated; a restore that cannot be done fails with
/// the server's reason.
/// </summary>
public class TrashHubTests
{
    private const string Profile = "trash";

    [Fact]
    public async Task DeleteAndRestore_OverTheHub_KeepTheId_AndSayWhatHappened()
    {
        var workDir = ServerProcess.CreateWorkDir("trashhub");
        var scriptPath = Path.Combine(workDir, "fake-claude.script");
        new FakeScript().EmitInit().AwaitStdin().Save(scriptPath);
        var environment = new Dictionary<string, string>
        {
            [FakeClaudeEnvironment.Script] = scriptPath,
            [FakeClaudeEnvironment.Record] = "fake-claude.jsonl",
        };
        foreach (var (root, shared) in new[] { ("assistant", true), ("worktrees", false) })
        {
            var configDir = Path.Combine(workDir, "roots", root, ".godmode-root");
            Directory.CreateDirectory(configDir);
            File.WriteAllText(Path.Combine(configDir, "config.json"),
                JsonSerializer.Serialize(new { profileName = Profile, sharedFolder = shared, transient = shared, environment }));
        }

        var server = ServerProcess.Start(workDir, $"http://127.0.0.1:{ServerProcess.GetFreePort()}",
            environment: new Dictionary<string, string> { ["Claude__Executable"] = LifecycleHarness.FakeClaudePath });
        try
        {
            var baseUrl = await server.WaitForListeningUrlAsync();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TestTimeouts.Request };
            await server.WaitForHealthyAsync(http);
            await using var client = new ServerHubClient(baseUrl);
            var pushes = new ConcurrentQueue<(string Method, string Id)>();
            client.Hub.On<ProjectStatus>(nameof(IProjectHubClient.ProjectCreated), status => pushes.Enqueue(("created", status.Id)));
            client.Hub.On<string>(nameof(IProjectHubClient.ProjectDeleted), id => pushes.Enqueue(("deleted", id)));
            await client.StartAsync();

            async Task<ProjectStatus> Create(string root, string name) =>
                (await client.Hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), Profile, root, null,
                    new Dictionary<string, JsonElement>
                    {
                        ["name"] = JsonSerializer.SerializeToElement(name),
                        ["prompt"] = JsonSerializer.SerializeToElement("Start"),
                    })).Project!;

            var roots = await client.Hub.InvokeAsync<ProjectRootInfo[]>(nameof(IProjectHub.ListProjectRoots));
            Assert.True(roots.Single(r => r.Name == "assistant").Actions!.Single().Transient);
            Assert.False(roots.Single(r => r.Name == "worktrees").Actions!.Single().Transient);

            var chat = await Create("assistant", "chat");
            await Create("assistant", "other");
            var worktree = await Create("worktrees", "feature");
            var listed = await client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects));
            Assert.True(listed.Single(p => p.Id == chat.Id).SharedFolder);
            Assert.False(listed.Single(p => p.Id == worktree.Id).SharedFolder);

            var deleted = await client.Hub.InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.DeleteProject), chat.Id, true);
            Assert.True(deleted.Trashed);
            var restored = await client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.RestoreProject), chat.Id);
            Assert.Equal(chat.Id, restored.Id);
            Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(pushes.Contains(("created", chat.Id)))),
                $"no ProjectCreated for the restored session; pushed: {string.Join(", ", pushes)}\n{server.Output}");
            Assert.Contains(("deleted", chat.Id), pushes);
            Assert.Contains(await client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)), p => p.Id == chat.Id);

            Assert.False((await client.Hub.InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.DeleteProject), worktree.Id, true)).Trashed);
            var refused = await Assert.ThrowsAsync<HubException>(() => client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.RestoreProject), worktree.Id));
            Assert.Contains("not in the trash", refused.Message);
        }
        finally
        {
            server.Dispose();
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
