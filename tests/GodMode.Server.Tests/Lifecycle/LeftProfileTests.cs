using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A session whose root moved to another profile while its claude ran is still its old profile's until
/// a read of the roots gives it its new ID: its status script and its launch get that profile's
/// environment, which the server's config still has though the snapshot lists only profiles with roots
/// (issue #342). Its delete script does too (<see cref="TrashTests"/>).
/// </summary>
public class LeftProfileTests
{
    private const string Variable = "LEFT_PROFILE_TEST";
    private const string OldProfiles = "the old profile's";

    /// <summary>Writes what the profile's variable is to <c>status-env.txt</c> in the root, and reports no pull request.</summary>
    private const string StatusScript = """
        $ErrorActionPreference = 'Stop'
        Set-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'status-env.txt') -Value "ran with $env:LEFT_PROFILE_TEST"
        '{}'
        """;

    [Fact]
    public async Task StatusScript_OfASessionWhoseRootMovedProfile_GetsItsProfilesEnvironment()
    {
        await using var harness = await MovedAndStoppedAsync();
        Assert.Equal($"ran with {OldProfiles}", await StatusScriptRanAsync(harness));
    }

    [Fact]
    public async Task Resume_OfASessionWhoseRootMovedProfile_LaunchesWithItsProfilesEnvironment()
    {
        await using var harness = await MovedAndStoppedAsync();
        var id = Assert.Single(await harness.Projects.ListProjectsAsync()).Id;
        Assert.StartsWith($"{LifecycleHarness.ProfileName}/", id);

        await harness.Projects.ResumeProjectAsync(id);
        var resume = await harness.WaitForLaunchAsync(id, _ => true, index: 1);
        Assert.Equal(OldProfiles, resume.Environment.GetValueOrDefault(Variable));
    }

    /// <summary>
    /// A session launched under the harness's profile, with its variable; then its root moves to a profile
    /// with no environment, two reads of the roots agree on it, and the session, whose claude runs, keeps its
    /// ID. Then it is stopped, which runs its status script, and no read of the roots has given it its new ID.
    /// </summary>
    private static async Task<LifecycleHarness> MovedAndStoppedAsync()
    {
        var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["status"] = "scripts/status" },
            settings: new Dictionary<string, string?> { [ProjectManager.RootsPollSetting] = "0" },
            profileEnvironment: new Dictionary<string, string> { [Variable] = OldProfiles });
        var scripts = Path.Combine(harness.RootPath, ".godmode-root", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "status.ps1"), StatusScript);
        await harness.Projects.RecoverProjectsAsync();
        var running = await harness.CreateProjectAsync("running");
        var create = await harness.WaitForStdinAsync(running.Id);
        Assert.Equal(OldProfiles, create.Environment[Variable]);

        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(config))!;
        edited["profileName"] = JsonSerializer.SerializeToElement("renamed");
        File.WriteAllText(config, JsonSerializer.Serialize(edited));
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProjectRootsAsync();
        Assert.DoesNotContain(await harness.Projects.ListProfilesAsync(), profile => profile.Name == LifecycleHarness.ProfileName);
        File.Delete(Told(harness));

        await harness.Projects.StopProjectAsync(running.Id);
        await harness.WaitForStateAsync(running.Id, ProjectState.Stopped);
        return harness;
    }

    /// <summary>What the status script the stop ran wrote, once it has.</summary>
    private static async Task<string> StatusScriptRanAsync(LifecycleHarness harness)
    {
        var ran = "";
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult((ran = ReadTold(Told(harness))).Length > 0), null,
            () => $"the status script did not run on the stop.\n{string.Join("\n", harness.Warnings)}");
        return ran;
    }

    private static string Told(LifecycleHarness harness) => Path.Combine(harness.RootPath, "status-env.txt");

    /// <summary>What the status script wrote, empty until it has, or while it writes.</summary>
    private static string ReadTold(string path)
    {
        try { return File.Exists(path) ? LifecycleHarness.ReadShared(path).Trim() : ""; }
        catch (IOException) { return ""; }
    }
}
