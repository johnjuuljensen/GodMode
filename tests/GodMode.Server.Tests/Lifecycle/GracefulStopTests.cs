using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A stop is graceful first: claude is interrupted (Ctrl+Break in its own console on Windows,
/// SIGINT to its process group elsewhere) and its input closed, and only what has not exited within
/// the grace period is killed, with its whole process tree. Sessions are off the server's console.
/// </summary>
public class GracefulStopTests
{
    /// <summary>What the fake records for the interrupt a stop sends.</summary>
    private static string StopInterrupt => OperatingSystem.IsWindows() ? "SIGQUIT" : "SIGINT";

    /// <summary>A claude in the middle of a turn: its input closing would not end it, only an interrupt or a kill.</summary>
    private static FakeScript Working(FakeScript? before = null) => (before ?? new FakeScript()).EmitInit().AwaitStdin().Sleep(120_000);

    private static Dictionary<string, string?> Grace(int seconds) => new() { [ClaudeProcessManager.StopGracePeriodSetting] = seconds.ToString() };

    /// <summary>
    /// A claude that exits when interrupted, as claude does: it is never killed. It ends its turn as
    /// interrupted, which is not the session failing: the project goes to Stopped, never through Error.
    /// </summary>
    [Fact]
    public async Task Stop_InterruptsFirst_AndAClaudeThatExitsOnItIsNeverKilled()
    {
        await using var harness = new LifecycleHarness(Working(), settings: Grace(30));
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);
        var pushedBefore = harness.Hub.StatusPushes(created.Id).Count;

        var elapsed = Stopwatch.StartNew();
        await harness.Projects.StopProjectAsync(created.Id);
        elapsed.Stop();

        var stopped = harness.Launches(created.Id)[0];
        Assert.Equal([StopInterrupt], stopped.Interrupts);
        Assert.True(stopped.ExitCode == 0, $"the fake did not exit by itself (a killed fake records no exit).\n{harness.Describe(created.Id)}");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"the stop took {elapsed.Elapsed}, as long as a kill after the 30 s grace period");
        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid));
        Assert.Contains("[Request interrupted by user]", harness.ReadOutputFile(created.Id));
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id).Skip(pushedBefore), s => s.State == ProjectState.Error);
        Assert.Empty(harness.Projects.GetAttention());
    }

    [Fact]
    public async Task Stop_KillsAClaudeThatIgnoresTheInterrupt_AfterTheGracePeriod()
    {
        await using var harness = new LifecycleHarness(Working(new FakeScript().IgnoreInterrupt()), settings: Grace(5));
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        var elapsed = Stopwatch.StartNew();
        await harness.Projects.StopProjectAsync(created.Id);
        elapsed.Stop();

        var stopped = harness.Launches(created.Id)[0];
        Assert.Equal([StopInterrupt], stopped.Interrupts);
        Assert.Null(stopped.ExitCode);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(4.9), $"the stop took {elapsed.Elapsed}, less than the 5 s grace period");
        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after Stop");
        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    /// <summary>
    /// The stop takes claude's whole tree: a child it started, and a child whose parent is gone
    /// (re-parented), which a walk of claude's children cannot find. Both ignore the interrupt, so
    /// only the kill of the process group or Job Object can end them: after claude exits on the
    /// interrupt, or with claude, when it ignores it and outlives the grace period.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_KillsClaudesWholeTree_AChildThatReparentedIncluded(bool claudeIgnoresTheInterrupt)
    {
        var script = claudeIgnoresTheInterrupt ? new FakeScript().IgnoreInterrupt() : new FakeScript();
        await using var harness = new LifecycleHarness(script.EmitInit().AwaitStdin().SpawnChild().SpawnChild(detached: true).Sleep(120_000),
            settings: Grace(5));
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForLaunchAsync(created.Id, l => l.Children.Count == 2);
        Assert.All(launch.Children, pid => Assert.True(LifecycleHarness.IsProcessAlive(pid), $"child {pid} is not running"));

        await harness.Projects.StopProjectAsync(created.Id);

        Assert.Equal(claudeIgnoresTheInterrupt ? null : 0, harness.Launches(created.Id)[0].ExitCode);
        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after Stop");
        var alive = new List<int>();
        await LifecycleHarness.WaitForAsync(() =>
        {
            alive = [.. launch.Children.Where(LifecycleHarness.IsProcessAlive)];
            return Task.FromResult(alive.Count == 0);
        }, TimeSpan.FromSeconds(5));
        Assert.True(alive.Count == 0, $"children {string.Join(", ", alive)} of fake claude (pid {launch.Pid}; child {launch.Children[0]}, re-parented {launch.Children[1]}) outlived the stop");
    }

    /// <summary>
    /// A child that left claude's process group (a session of its own, as a detached spawn or a daemon
    /// has) escapes the group's kill on Linux: the stop finds it by walking claude's tree while claude
    /// still runs. On Windows the Job Object holds it all the same.
    /// </summary>
    [Fact]
    public async Task Stop_KillsAChildThatLeftClaudesProcessGroup()
    {
        await using var harness = new LifecycleHarness(Working(new FakeScript().IgnoreInterrupt().SpawnChild(ownSession: true)), settings: Grace(5));
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForLaunchAsync(created.Id, l => l.Children.Count == 1 && l.Stdin.Count == 1);
        var child = launch.Children[0];
        Assert.True(LifecycleHarness.IsProcessAlive(child), $"child {child} is not running");

        await harness.Projects.StopProjectAsync(created.Id);

        Assert.False(LifecycleHarness.IsProcessAlive(launch.Pid), $"fake claude (pid {launch.Pid}) is still running after Stop");
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(!LifecycleHarness.IsProcessAlive(child)), TimeSpan.FromSeconds(5)),
            $"child {child} of fake claude (pid {launch.Pid}), in a session of its own, outlived the stop");
    }

    /// <summary>
    /// A Ctrl+C in the terminal of a server (a console of its own on Windows, a process group of its
    /// own on Linux) reaches the server, whose shutdown then stops its sessions: the session gets one
    /// interrupt, the server's. On the server's console it would get the Ctrl+C itself first.
    /// </summary>
    [Fact]
    public async Task CtrlCInTheServersTerminal_ReachesTheSessionOnlyThroughTheServersShutdown()
    {
        const string profile = "console";
        const string root = "lifecycle";
        var workDir = ServerProcess.CreateWorkDir("console");
        var scriptPath = Path.Combine(workDir, "fake-claude.script");
        Working(new FakeScript().IgnoreInterrupt()).Save(scriptPath);
        var rootConfig = Path.Combine(workDir, "roots", root, ".godmode-root");
        Directory.CreateDirectory(rootConfig);
        File.WriteAllText(Path.Combine(rootConfig, "config.json"), JsonSerializer.Serialize(new
        {
            profileName = profile,
            environment = new Dictionary<string, string>
            {
                [FakeClaudeEnvironment.Script] = scriptPath,
                [FakeClaudeEnvironment.Record] = "fake-claude.jsonl",
            },
        }));

        // A server started from a shell that ignores Ctrl+C would ignore it too (Windows passes that on)
        if (OperatingSystem.IsWindows()) SetConsoleCtrlHandler(IntPtr.Zero, false);
        var baseUrl = $"http://127.0.0.1:{ServerProcess.GetFreePort()}";
        var server = ServerProcess.Start(workDir, baseUrl, ownTerminal: true, environment: new Dictionary<string, string>
        {
            ["Claude__Executable"] = LifecycleHarness.FakeClaudePath,
            [ClaudeProcessManager.StopGracePeriodSetting] = "5",
        });
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            await server.WaitForHealthyAsync(http);
            await using var client = new ServerHubClient(baseUrl);
            await client.StartAsync();
            var created = await client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.CreateProject), profile, root, null,
                new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("p1"),
                    ["prompt"] = JsonSerializer.SerializeToElement("Work on it"),
                });
            var record = Path.Combine(workDir, "roots", root, "p1", "fake-claude.jsonl");
            FakeLaunch? launch = null;
            Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult((launch = FakeRecording.Read(record).FirstOrDefault()) is { Stdin.Count: 1 })),
                $"the fake never read its prompt.\n{server.Output}");
            await client.DisposeAsync();

            PressCtrlC(server.ProcessId);

            Assert.True(await server.WaitForExitAsync(TimeSpan.FromSeconds(30)), $"the server did not stop on Ctrl+C.\n{server.Output}");
            var stopped = Assert.Single(FakeRecording.Read(record));
            Assert.Equal([StopInterrupt], stopped.Interrupts);
            Assert.False(LifecycleHarness.IsProcessAlive(launch!.Pid), $"fake claude (pid {launch.Pid}) outlived the server");
        }
        finally
        {
            server.Dispose();
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    /// <summary>Ctrl+C in the server's terminal: its console on Windows, its foreground process group elsewhere.</summary>
    private static void PressCtrlC(int serverPid)
    {
        if (OperatingSystem.IsWindows())
        {
            using var raising = Process.Start(new ProcessStartInfo(LifecycleHarness.FakeClaudePath, [FakeClaudeEnvironment.RaiseFlag, serverPid.ToString(), "ctrl-c"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            })!;
            var error = raising.StandardError.ReadToEnd();
            raising.WaitForExit();
            Assert.True(raising.ExitCode == 0, $"raising Ctrl+C in the console of {serverPid} exited {raising.ExitCode}: {error}");
        }
        else
            Assert.Equal(0, kill(-serverPid, 2));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
