using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// Live roots in the real server, as it starts: once it has recovered its projects, a connected client
/// is pushed <see cref="IProjectHubClient.RootsChanged"/> for a root folder created in a scan folder (the
/// poll), and for an explicit root written into the instance's config file (its reload on change).
/// </summary>
public class LiveRootsServerTests
{
    private static void WriteRoot(string path, string profile)
    {
        Directory.CreateDirectory(Path.Combine(path, ".godmode-root"));
        File.WriteAllText(Path.Combine(path, ".godmode-root", "config.json"), JsonSerializer.Serialize(new { profileName = profile }));
    }

    /// <summary>Starts the server on its config file, and a client that keeps every RootsChanged it is pushed.</summary>
    private static async Task<(ServerProcess Server, ServerHubClient Client, ConcurrentQueue<ProjectRootInfo[]> Pushes)> StartAsync(string workDir, object config)
    {
        File.WriteAllText(Path.Combine(workDir, "main.json"), JsonSerializer.Serialize(config));
        var server = ServerProcess.Start(workDir, "http://127.0.0.1:0", rootsOnCommandLine: false, arguments: ["--config", "main.json"]);
        var url = await server.WaitForListeningUrlAsync();
        using (var http = new HttpClient { BaseAddress = new Uri(url) })
            await server.WaitForHealthyAsync(http);
        var client = new ServerHubClient(url);
        var pushes = new ConcurrentQueue<ProjectRootInfo[]>();
        client.Hub.On<ProjectRootInfo[], ProfileInfo[]>(nameof(IProjectHubClient.RootsChanged), (roots, _) => pushes.Enqueue(roots));
        await client.StartAsync();
        // Live once the recovery has run, which starts with the server
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(server.Output.Contains("Roots are read again"))), server.Output);
        return (server, client, pushes);
    }

    private static async Task AssertPushedAsync(ServerProcess server, ConcurrentQueue<ProjectRootInfo[]> pushes, string root, string profile) =>
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(pushes.Any(roots => roots.Any(r => r.Name == root && r.ProfileName == profile)))),
            $"no RootsChanged listing {profile}/{root}\n{server.Output}");

    [Fact]
    public async Task RootFolderCreatedInAScanFolder_IsPushedToAConnectedClient()
    {
        var workDir = ServerProcess.CreateWorkDir("live-roots-poll");
        try
        {
            var (server, client, pushes) = await StartAsync(workDir, new Dictionary<string, object>
            {
                ["Roots"] = new { Scan = new { main = Path.Combine(workDir, "roots") } },
                [ProjectManager.RootsPollSetting] = 0.5,
            });
            using (server)
            await using (client)
            {
                WriteRoot(Path.Combine(workDir, "roots", "fresh"), "fresh-profile");
                await AssertPushedAsync(server, pushes, "fresh", "fresh-profile");
            }
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExplicitRootWrittenIntoTheConfigFile_IsPushedOnItsReload_WithThePollOff()
    {
        var workDir = ServerProcess.CreateWorkDir("live-roots-reload");
        try
        {
            var (server, client, pushes) = await StartAsync(workDir, new Dictionary<string, object> { [ProjectManager.RootsPollSetting] = 0 });
            using (server)
            await using (client)
            {
                var notes = Path.Combine(workDir, "notes");
                WriteRoot(notes, "Private");
                File.WriteAllText(Path.Combine(workDir, "main.json"), JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [ProjectManager.RootsPollSetting] = 0,
                    ["Roots"] = new { Explicit = new { notes = new { Path = notes } } },
                }));
                await AssertPushedAsync(server, pushes, "notes", "Private");
            }
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
