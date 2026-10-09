using System.Diagnostics;
using System.Text.Json;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// The reap a server runs before it recovers a root's sessions (<see cref="SessionProcessTree.ReapOrphansAsync"/>),
/// on processes of its own rather than a server's: Linux only, where a session's process group outlives a server
/// that dies (issue #280). <see cref="CrashedServerTests"/> has it end to end.
/// </summary>
public class ProcessGroupReapTests
{
    /// <summary>
    /// A recorded group whose processes carry the session's launch is ended whole: what ignores SIGTERM
    /// is killed after the grace period, and the record goes.
    /// </summary>
    [Fact]
    public async Task Reap_EndsARecordedGroupThatCarriesItsLaunch_KillingWhatIgnoresSigterm()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewRoot();
        // A shell that ignores SIGTERM, and a sleep it starts, which inherits that
        using var group = StartOwnGroup("sh", ["-c", "trap '' TERM; sleep 600 & wait"], launch: "abc");
        var recordPath = WriteRecord(root, "s1", group.Id, "abc");
        try
        {
            var elapsed = Stopwatch.StartNew();
            await SessionProcessTree.ReapOrphansAsync([root], NullLogger.Instance, TimeSpan.FromSeconds(1));

            Assert.True(group.WaitForExit(5_000), $"the group's leader (pid {group.Id}) outlived the reap");
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(0.9), $"the reap took {elapsed.Elapsed}, less than its grace period, for a group that ignores SIGTERM");
            Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(GroupMembers(group.Id).Length == 0), TimeSpan.FromSeconds(5)),
                $"processes {string.Join(", ", GroupMembers(group.Id))} of group {group.Id} outlived the reap");
            Assert.False(File.Exists(recordPath));
        }
        finally
        {
            KillGroupQuietly(group.Id);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The PID/PGID reuse guard: a group id is a pid, which the kernel can give another group once the
    /// session's is gone. A group none of whose processes carries the recorded launch is left alone, and
    /// only the record goes.
    /// </summary>
    [Fact]
    public async Task Reap_LeavesAGroupThatDoesNotCarryTheRecordedLaunch()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewRoot();
        using var other = StartOwnGroup("sleep", ["600"], launch: "someone-else");
        var recordPath = WriteRecord(root, "s1", other.Id, "abc");
        try
        {
            await SessionProcessTree.ReapOrphansAsync([root], NullLogger.Instance, TimeSpan.FromSeconds(1));

            Assert.False(other.HasExited, $"the reap killed group {other.Id}, which is not the session's");
            Assert.False(File.Exists(recordPath));
        }
        finally
        {
            KillGroupQuietly(other.Id);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A session this process runs is no orphan: its record stays, and so does its claude.</summary>
    [Fact]
    public async Task Reap_LeavesALaunchOfThisProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewRoot();
        var recordPath = SessionProcessTree.RecordPathFor(root, "s1");
        var tree = SessionProcessTree.Create(NullLogger.Instance, recordPath);
        var start = new ProcessStartInfo("sleep", ["600"]) { UseShellExecute = false };
        tree.Prepare(start);
        using var process = Process.Start(start)!;
        tree.Attach(process);
        try
        {
            Assert.True(File.Exists(recordPath), "the launch recorded no group");

            await SessionProcessTree.ReapOrphansAsync([root], NullLogger.Instance, TimeSpan.FromSeconds(1));

            Assert.False(process.HasExited, "the reap killed a launch of this process");
            Assert.True(File.Exists(recordPath));

            tree.Dispose();
            Assert.True(process.WaitForExit(5_000), "the tree's dispose left its group running");
            Assert.False(File.Exists(recordPath), "the tree's dispose left its record");
        }
        finally
        {
            tree.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"godmode-reap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteRecord(string root, string sessionId, int group, string launch)
    {
        var path = SessionProcessTree.RecordPathFor(root, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new SessionProcessTree.GroupRecord(group, launch)));
        return path;
    }

    /// <summary>A program in a session and process group of its own (setsid, in place: its pid is the group's), carrying <paramref name="launch"/>.</summary>
    private static Process StartOwnGroup(string program, IReadOnlyList<string> args, string launch)
    {
        var start = new ProcessStartInfo("setsid", [program, .. args]) { UseShellExecute = false };
        start.Environment[SessionProcessTree.LaunchVariable] = launch;
        return Process.Start(start)!;
    }

    /// <summary>The live, non-zombie processes in group <paramref name="group"/>.</summary>
    private static int[] GroupMembers(int group) =>
        [.. Directory.EnumerateDirectories("/proc")
            .Select(dir => int.TryParse(Path.GetFileName(dir), out var pid) ? pid : 0)
            .Where(pid => pid > 0 && Stat(pid) is [var state, _, var pgrp, ..] && state != "Z" && pgrp == group.ToString())];

    private static string[]? Stat(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        }
        catch (IOException) { return null; }
    }

    private static void KillGroupQuietly(int group)
    {
        try { Process.Start("kill", ["-KILL", "--", $"-{group}"])?.WaitForExit(5_000); } catch { /* gone */ }
    }
}
