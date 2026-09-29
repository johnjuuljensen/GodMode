using System.Diagnostics;
using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// One server per root: a server holds a lock on each root it manages, and a second server on the
/// same roots (a dev server run from a worktree) leaves every root the first one holds alone, until
/// the first one has gone.
/// </summary>
public class OneServerPerRootTests
{
    private const string OtherRoot = "other-root";

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private static Dictionary<string, string?> Instance(string name, string? rootsDir = null)
    {
        var settings = new Dictionary<string, string?> { [ProjectManager.InstanceSetting] = name };
        if (rootsDir != null) settings["ProjectRootsDir"] = rootsDir;
        return settings;
    }

    private static string[] SkippedLines(LifecycleHarness harness) =>
        harness.Warnings.Where(line => line.Contains("held by another server")).ToArray();

    [Fact]
    public async Task SecondServer_ListsNoneOfTheHeldRoots_LogsEachOnce_AndPicksThemUpOnceTheFirstStops()
    {
        await using var main = new LifecycleHarness(Waiting(), settings: Instance("main"), extraRoots: [(OtherRoot, "other")]);
        await using var dev = new LifecycleHarness(Waiting(), settings: Instance("dev", main.RootsDir));

        // Each rebuilds its snapshot several times, taking turns: a held root stays held
        for (var i = 0; i < 3; i++)
        {
            var mainRoots = await main.Projects.ListProjectRootsAsync();
            Assert.Contains(mainRoots, root => root.Name == LifecycleHarness.RootName);
            Assert.Contains(mainRoots, root => root.Name == OtherRoot);
            Assert.DoesNotContain(await dev.Projects.ListProjectRootsAsync(), root => root.Name is LifecycleHarness.RootName or OtherRoot);
        }

        // Nor their profiles, which have no other root there
        Assert.DoesNotContain(await dev.Projects.ListProfilesAsync(), profile => profile.Name is LifecycleHarness.ProfileName or "other");

        var skipped = SkippedLines(dev);
        Assert.Equal(2, skipped.Length);
        Assert.All(skipped, line => Assert.Contains($"instance main, process {Environment.ProcessId}", line));
        Assert.Contains(skipped, line => line.Contains($"/{LifecycleHarness.RootName} at"));
        Assert.Contains(skipped, line => line.Contains($"/{OtherRoot} at"));

        main.StopHost();

        var picked = await dev.Projects.ListProjectRootsAsync();
        Assert.Contains(picked, root => root.Name == LifecycleHarness.RootName);
        Assert.Contains(picked, root => root.Name == OtherRoot);
        Assert.Contains(await dev.Projects.ListProfilesAsync(), profile => profile.Name == LifecycleHarness.ProfileName);
        Assert.Equal(2, SkippedLines(dev).Length);
    }

    /// <summary>A server restarted over its roots holds them again: the one that stopped let them go.</summary>
    [Fact]
    public async Task RestartedServer_HoldsItsRootsAgain()
    {
        await using var harness = new LifecycleHarness(Waiting());

        await harness.RestartAsync();

        Assert.Contains(await harness.Projects.ListProjectRootsAsync(), root => root.Name == LifecycleHarness.RootName);
        Assert.Empty(SkippedLines(harness));
    }

    /// <summary>A root whose folder is a checkout: the lock and its holder never show in <c>git status</c>.</summary>
    [Fact]
    public async Task HeldRootUnderGit_ShowsNothingOfTheLock()
    {
        await using var harness = new LifecycleHarness(Waiting());
        Git(harness.RootPath, "init", "--quiet");
        Assert.True(File.Exists(Path.Combine(harness.RootPath, "logs", RootLock.LockFileName)));
        Assert.True(File.Exists(Path.Combine(harness.RootPath, "logs", RootLock.HolderFileName)));

        var status = Git(harness.RootPath, "status", "--porcelain", "--untracked-files=all");

        Assert.Equal([".godmode-root/config.json"], status.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line[3..]));
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var git = Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {git.StandardError.ReadToEnd()}");
        return output;
    }
}
