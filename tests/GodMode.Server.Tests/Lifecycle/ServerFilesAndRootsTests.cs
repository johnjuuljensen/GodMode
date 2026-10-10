using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Auth;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// The server's own files and the roots around them (#344): the instance's config file, like the key
/// file, is never where sessions work; the fallback root is locked like any root; a reload of an invalid
/// config file keeps the config it had; the roots' poll has a floor; and a folder whose trash holds a
/// shared session's state is no folder for a session whose delete removes it.
/// </summary>
public sealed class ServerFilesAndRootsTests : IDisposable
{
    private readonly string _elsewhere = ServerProcess.CreateWorkDir("server-files");

    public void Dispose() => ServerProcess.DeleteWorkDir(_elsewhere);

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private string Elsewhere(params string[] parts) => Path.Combine([_elsewhere, .. parts]);

    private static string WriteRoot(string path, string profile = "p")
    {
        var configDir = Path.Combine(path, ".godmode-root");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(new { profileName = profile }));
        return path;
    }

    // ── The instance's config file: never under a root ──

    /// <summary>The start refuses an instance config file in the tree of a root source, as it refuses the key file there.</summary>
    [Fact]
    public void ConfigFileUnderAScanFolder_StopsTheStart()
    {
        var file = Elsewhere("roots", "main.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { Roots = new { Scan = new { mine = Elsewhere("roots") } } }));
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [InstanceConfig.CommandLineSetting] = file });

        var ex = Assert.Throws<StartupConfigurationException>(() => InstanceConfig.AddTo(configuration, configuration, "Production"));

        Assert.Contains(file, ex.Message);
        Assert.Contains($"Roots:Scan:mine ({Elsewhere("roots")})", ex.Message);
    }

    /// <summary>One added to the config while the server runs, over the config file's folder, is left out, logged once.</summary>
    [Fact]
    public async Task RootSourceAddedLater_WhoseTreeHoldsTheConfigFile_IsLeftOut()
    {
        var instanceFile = Elsewhere("config", "main.json");
        Directory.CreateDirectory(Path.GetDirectoryName(instanceFile)!);
        File.WriteAllText(instanceFile, "{}");
        WriteRoot(Elsewhere("config", "beside-the-file"));
        await using var harness = new LifecycleHarness(Waiting(),
            settings: new Dictionary<string, string?> { [InstanceConfig.CommandLineSetting] = instanceFile, [ProjectManager.RootsPollSetting] = "0" },
            configFiles: [instanceFile]);

        File.WriteAllText(instanceFile, JsonSerializer.Serialize(new { Roots = new { Scan = new { config = Elsewhere("config") } } }));
        harness.ReloadConfiguration();

        for (var i = 0; i < 2; i++)
        {
            var names = (await harness.Projects.ListProjectRootsAsync()).Select(root => root.Name).ToArray();
            Assert.DoesNotContain("beside-the-file", names);
            Assert.Contains(LifecycleHarness.RootName, names);
        }
        var leftOut = Assert.Single(harness.Warnings, line => line.Contains("is left out"));
        Assert.Contains($"Roots:Scan:config ({Elsewhere("config")})", leftOut);
        Assert.Contains(instanceFile, leftOut);
    }

    /// <summary>
    /// The key file in the fallback root, which is <c>projects</c> beside it, can only be a key file named
    /// <c>projects</c>: the start refuses it, as it refuses one under any root.
    /// </summary>
    [Fact]
    public void KeyFileThatIsTheFallbackRoot_StopsTheStart()
    {
        var keyFile = Elsewhere("data", ServerDataDirectory.FallbackRootName);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ApiKeyFile.PathSetting] = keyFile }).Build();

        var ex = Assert.Throws<StartupConfigurationException>(() => ApiKeyFile.PathFrom(config));

        Assert.Contains(keyFile, ex.Message);
        Assert.Contains("fallback root", ex.Message);
    }

    // ── The fallback root: locked like any root ──

    /// <summary>
    /// Two servers with no roots and one data directory have one fallback root: the first holds it, and
    /// the second leaves it alone, as it leaves any root another server holds, until the first has gone.
    /// </summary>
    [Fact]
    public async Task FallbackRoot_HeldByAnotherServer_IsLeftAlone()
    {
        var dataDir = Elsewhere("data");
        var fallback = Path.Combine(dataDir, ServerDataDirectory.FallbackRootName);
        Dictionary<string, string?> Settings(string instance) => new()
        {
            [ProjectManager.InstanceSetting] = instance,
            [LifecycleHarness.ScanSetting] = "",
            [ApiKeyFile.PathSetting] = Path.Combine(dataDir, ApiKeyFile.FileName),
        };
        await using var first = new LifecycleHarness(Waiting(), settings: Settings("first"));
        await using var second = new LifecycleHarness(Waiting(), settings: Settings("second"));

        Assert.Contains(await first.Projects.ListProjectRootsAsync(), root => (root.ProfileName, root.Name) == ("Default", "default"));
        Assert.True(File.Exists(Path.Combine(fallback, "logs", RootLock.LockFileName)), "the fallback root is not locked");
        Assert.DoesNotContain(await second.Projects.ListProjectRootsAsync(), root => root.Name == "default");
        Assert.Contains(second.Warnings, line => line.Contains($"Default/default at {fallback} is held by another server (instance first"));

        first.StopHost();

        Assert.Contains(await second.Projects.ListProjectRootsAsync(), root => (root.ProfileName, root.Name) == ("Default", "default"));
    }

    // ── A reload of an invalid instance config file ──

    /// <summary>
    /// The instance's file saved with an error in it (a half-done edit) keeps the config the file last
    /// had, rather than none of it, and the error is reported; a fixed save is read as usual.
    /// </summary>
    [Fact]
    public async Task InvalidConfigFileOnReload_KeepsTheLastGoodConfig_AndSaysWhy()
    {
        var file = Elsewhere("config", "main.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "Instance": "good" }""");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [InstanceConfig.CommandLineSetting] = file });
        InstanceConfig.AddTo(configuration, configuration, "Production");
        var refused = new List<string>();
        InstanceConfig.ReportRefusedReloads(configuration, (path, error) => { lock (refused) refused.Add($"{path}: {error.Message}"); });
        Assert.Equal("good", configuration["Instance"]);

        // Refused, or, as before #344, read as a file with nothing in it
        File.WriteAllText(file, """{ "Instance": "half-sa""");
        await LifecycleHarness.WaitUntilAsync(() =>
        {
            lock (refused) return Task.FromResult(refused.Count > 0 || configuration["Instance"] != "good");
        }, null, () => "the invalid save was neither refused nor read");
        Assert.Equal("good", configuration["Instance"]);
        lock (refused) Assert.Contains(refused, line => line.StartsWith(file));

        await SaveAndAwaitReloadAsync(configuration, file, """{ "Instance": "fixed" }""");
        Assert.Equal("fixed", configuration["Instance"]);
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="file"/>, and waits for the configuration's reload of it.</summary>
    private static async Task SaveAndAwaitReloadAsync(IConfiguration configuration, string file, string content)
    {
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = ChangeToken.OnChange(configuration.GetReloadToken, () => reloaded.TrySetResult());
        File.WriteAllText(file, content);
        await reloaded.Task.WaitAsync(LifecycleHarness.DefaultTimeout);
    }

    // ── RootsPollSeconds: a floor ──

    /// <summary>A poll faster than a second is a second: the roots are not read again many times a second, and the raise is logged.</summary>
    [Fact]
    public async Task RootsPollBelowTheFloor_IsRaisedToIt()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?> { [ProjectManager.RootsPollSetting] = "0.001" });
        await harness.Projects.RecoverProjectsAsync();

        Assert.Contains(harness.AllLogs, line => line.Contains($"every 1s ({ProjectManager.RootsPollSetting})"));
        Assert.Contains(harness.Warnings, line => line.Contains($"{ProjectManager.RootsPollSetting} (0.001) is below its floor"));
    }

    // ── The trash counts for a folder a session would own ──

    /// <summary>
    /// A folder whose sessions all share it, deleted into its trash, is no folder for a session of an action
    /// that does not share (a "Reuse folder" create): its delete would remove the folder, the trash with it,
    /// and nothing could be restored. A shared create may still join it, and a restore still works.
    /// </summary>
    [Fact]
    public async Task FolderWithASharedSessionInItsTrash_IsRefusedToASessionThatOwnsItsFolder()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var godModeRoot = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(godModeRoot, "config.shared.json"), """{ "sharedFolder": true }""");
        File.WriteAllText(Path.Combine(godModeRoot, "config.owned.json"), "{}");
        var shared = await CreateAsync(harness, "shared", reuse: false);
        await harness.WaitForStdinAsync(shared.Id);
        Assert.True((await harness.Projects.DeleteProjectAsync(shared.Id)).Trashed);

        var refused = await Assert.ThrowsAsync<ProjectInUseException>(() => CreateAsync(harness, "owned", reuse: true));

        Assert.Contains("trash", refused.Message);
        Assert.True(Directory.Exists(GodMode.ProjectFiles.SessionState.TrashedPathOf(Path.Combine(harness.RootPath, "workspace"), shared.Id.Split('/')[^1])),
            "the trashed session is gone");
        await harness.Projects.RestoreProjectAsync(shared.Id);
        Assert.NotNull(await CreateAsync(harness, "shared", reuse: true));
    }

    private static async Task<ProjectStatus> CreateAsync(LifecycleHarness harness, string action, bool reuse) =>
        (await harness.Projects.CreateProjectAsync(new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("workspace"),
                ["prompt"] = JsonSerializer.SerializeToElement("Say hello"),
                ["__reuseExisting"] = JsonSerializer.SerializeToElement(reuse),
            }, ActionName: action))).Project!;
}
