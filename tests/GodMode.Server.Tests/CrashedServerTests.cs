using System.Diagnostics;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// A server that dies without stopping its sessions (a crash, a SIGKILL) leaves no claude running into
/// the next server's sessions (issue #280). On Windows the Job Object dies with the server, and the
/// session with it. On Linux the session's process group outlives it, so the next server to hold the
/// root ends it, recorded in the root, before it recovers the root's sessions. macOS has neither: the
/// gap is documented, and these tests do not run there.
/// </summary>
public class CrashedServerTests
{
    private const string Profile = "crash";
    private const string Root = "lifecycle";

    /// <summary>
    /// The harness: a real server, a fake claude that has started a child, the server killed with no
    /// shutdown, a new server on the same roots. The old fake and its child are gone by the time the new
    /// server has recovered the session, before anything can resume it, and a resume runs one fake on it.
    /// </summary>
    [Fact]
    public async Task AfterACrash_TheOldClaudeAndItsChildAreGoneBeforeTheSessionIsRecovered_AndAResumeRunsOneClaude()
    {
        if (OperatingSystem.IsMacOS()) return;
        var workDir = ServerProcess.CreateWorkDir("crash");
        var scriptPath = Path.Combine(workDir, "fake-claude.script");
        new FakeScript().EmitInit().AwaitStdin().SpawnChild().EmitAssistant("Working on it").Sleep(120_000).Save(scriptPath);
        var rootPath = Path.Combine(workDir, "roots", Root);
        Directory.CreateDirectory(Path.Combine(rootPath, ".godmode-root"));
        File.WriteAllText(Path.Combine(rootPath, ".godmode-root", "config.json"), JsonSerializer.Serialize(new
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
            FakeLaunch? orphan = null;
            {
                var baseUrl = await first.WaitForListeningUrlAsync();
                using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
                await first.WaitForHealthyAsync(http);
                await using var client = new ServerHubClient(baseUrl);
                await client.StartAsync();
                var created = (await client.Hub.InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), Profile, Root, null,
                    new Dictionary<string, JsonElement>
                    {
                        ["name"] = JsonSerializer.SerializeToElement("p1"),
                        ["prompt"] = JsonSerializer.SerializeToElement("Start"),
                    })).Project!;
                projectId = created.Id;
                record = Path.Combine(ServerProcess.WorkingFolderOf(rootPath, projectId), "fake-claude.jsonl");
                Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(
                        (orphan = FakeRecording.Read(record).SingleOrDefault()) is { Stdin.Count: 1, Children.Count: 1 })),
                    $"the first launch never got its prompt and started its child.\n{first.Output}");
            }
            var child = orphan!.Children[0];

            first.Crash();
            // Linux: the crash left them running, else what follows proves nothing. Windows: the Job Object took them
            if (OperatingSystem.IsLinux())
            {
                Assert.True(LifecycleHarness.IsProcessAlive(orphan.Pid), $"fake claude (pid {orphan.Pid}) did not outlive the crash");
                Assert.True(LifecycleHarness.IsProcessAlive(child), $"its child (pid {child}) did not outlive the crash");
            }
            new FakeScript().EmitInit().AwaitStdin().AwaitStdin().Save(scriptPath);

            second = ServerProcess.Start(workDir, $"http://127.0.0.1:{ServerProcess.GetFreePort()}", environment: fake);
            var restartedUrl = await second.WaitForListeningUrlAsync();
            using var restartedHttp = new HttpClient { BaseAddress = new Uri(restartedUrl), Timeout = TimeSpan.FromSeconds(10) };
            await second.WaitForHealthyAsync(restartedHttp);
            await using var restarted = new ServerHubClient(restartedUrl);
            await restarted.StartAsync();
            Assert.True(await LifecycleHarness.WaitForAsync(async () =>
                    (await restarted.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Any(p => p.Id == projectId)),
                $"the restarted server did not recover {projectId}.\n{second.Output}");

            // Recovered: nothing could have resumed it yet, and the old claude and its child are gone already
            var leftAtRecovery = new[] { orphan.Pid, child }.Where(LifecycleHarness.IsProcessAlive).ToArray();

            await restarted.Hub.InvokeAsync(nameof(IProjectHub.ResumeProject), projectId);
            Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(FakeRecording.Read(record) is [_, _])),
                $"the resume started no claude.\n{second.Output}");
            var resumed = FakeRecording.Read(record)[1].Pid;
            var running = FakeRecording.Read(record).SelectMany(launch => launch.Children.Prepend(launch.Pid)).Where(LifecycleHarness.IsProcessAlive).ToArray();
            Assert.True(leftAtRecovery.Length == 0 && running.SequenceEqual([resumed]),
                $"running when the session was recovered: [{string.Join(", ", leftAtRecovery)}] (old fake {orphan.Pid}, its child {child}); " +
                $"running on the session after the resume: [{string.Join(", ", running)}] (the resumed fake is {resumed}).\n{second.Output}");
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
            // What a failure left running
            if (record != null && File.Exists(record))
                foreach (var pid in FakeRecording.Read(record).SelectMany(launch => launch.Children.Prepend(launch.Pid)))
                    KillQuietly(pid);
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch { /* gone */ }
    }
}
