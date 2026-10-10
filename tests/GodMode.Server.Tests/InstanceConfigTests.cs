using System.Text.Json;
using GodMode.Server.Auth;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// The real server's config, as it starts: its instance's config file (<c>--config</c>) over
/// appsettings, and nothing per user, so a dev server started without a file of its own never reads
/// the user's roots. And one server per root, across processes: a root a live server holds is left
/// alone by a second one, until the first has gone, however it went.
/// </summary>
public class InstanceConfigTests
{
    /// <summary>The <c>UserSecretsId</c> the server had: every developer machine kept its <c>ProjectRootsDir</c> under it.</summary>
    private const string FormerUserSecretsId = "eff7560e-7e44-46f7-b010-7ca1368e1689";

    private static void WriteRoot(string rootsDir, string name, string profile)
    {
        var configDir = Path.Combine(rootsDir, name, ".godmode-root");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(new { profileName = profile }));
    }

    private static void WriteJson(string path, object content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(content));
    }

    private static async Task<ProjectRootInfo[]> ListRootsAsync(string url)
    {
        await using var client = new ServerHubClient(url);
        await client.StartAsync();
        return await client.Hub.InvokeAsync<ProjectRootInfo[]>(nameof(IProjectHub.ListProjectRoots));
    }

    private static async Task<string> StartAsync(ServerProcess server)
    {
        var url = await server.WaitForListeningUrlAsync();
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        await server.WaitForHealthyAsync(http);
        return url;
    }

    [Fact]
    public async Task ConfigFile_OverridesAppSettings()
    {
        var workDir = ServerProcess.CreateWorkDir("instance-config");
        try
        {
            WriteRoot(Path.Combine(workDir, "appsettings-roots"), "from-appsettings", "p");
            WriteRoot(Path.Combine(workDir, "config-roots"), "from-config", "p");
            WriteJson(Path.Combine(workDir, "appsettings.json"), new { Roots = new { Scan = new { @default = "appsettings-roots" } }, Instance = "appsettings" });
            WriteJson(Path.Combine(workDir, "instances", "main.json"), new { Roots = new { Scan = new { @default = Path.Combine(workDir, "config-roots") } }, Instance = "main" });

            using var server = ServerProcess.Start(workDir, "http://127.0.0.1:0", rootsOnCommandLine: false,
                arguments: ["--config", Path.Combine("instances", "main.json")]);
            var roots = await ListRootsAsync(await StartAsync(server));

            Assert.Contains(roots, root => root.Name == "from-config");
            Assert.DoesNotContain(roots, root => root.Name == "from-appsettings");
            Assert.Contains("Server instance main", server.Output);
            Assert.Contains($"Config file: {Path.Combine(workDir, "instances", "main.json")}", server.Output);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ConfigFileFromTheEnvironment_IsRead()
    {
        var workDir = ServerProcess.CreateWorkDir("instance-config-env");
        try
        {
            WriteJson(Path.Combine(workDir, "main.json"), new { Instance = "from-env" });

            using var server = ServerProcess.Start(workDir, "http://127.0.0.1:0",
                environment: new Dictionary<string, string> { [InstanceConfig.EnvironmentVariable] = Path.Combine(workDir, "main.json") });
            await StartAsync(server);

            Assert.Contains("Server instance from-env", server.Output);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task NamedConfigFileThatDoesNotExist_RefusesToStart()
    {
        var workDir = ServerProcess.CreateWorkDir("instance-config-missing");
        try
        {
            using var server = ServerProcess.Start(workDir, "http://127.0.0.1:0", arguments: ["--config", "missing.json"]);

            Assert.True(await server.WaitForExitAsync(TimeSpan.FromSeconds(60)), server.Output);
            Assert.Equal(1, server.ExitCode);
            Assert.Contains(Path.Combine(workDir, "missing.json"), server.Output);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    /// <summary>
    /// A dev server run as <c>dotnet run</c> runs (Development) with no config file: the user secrets
    /// every worktree once shared, and the roots they name, are not read. It runs on the
    /// server's own appsettings.
    /// </summary>
    [Fact]
    public async Task WithoutAConfigFile_TheUsersRootsAreNotRead()
    {
        var workDir = ServerProcess.CreateWorkDir("instance-config-none");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(workDir, "appsettings.json"));
            var usersRoots = Path.Combine(workDir, "users-roots");
            WriteRoot(usersRoots, "users-root", "user");
            var appData = Path.Combine(workDir, "appdata");
            WriteJson(Path.Combine(appData, "Microsoft", "UserSecrets", FormerUserSecretsId, "secrets.json"),
                new Dictionary<string, string> { ["ProjectRootsDir"] = usersRoots, ["Roots:Scan:user"] = usersRoots });

            using var server = ServerProcess.Start(workDir, "http://127.0.0.1:0", rootsOnCommandLine: false,
                environment: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["APPDATA"] = appData });
            var roots = await ListRootsAsync(await StartAsync(server));

            Assert.DoesNotContain(roots, root => root.Name == "users-root");
            Assert.Contains("Config file: none", server.Output);
            Assert.Contains($"Roots from Roots:Scan:default: {Path.Combine(workDir, "roots")}", server.Output);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    /// <summary>
    /// A server with no roots has one, the <c>Default</c> profile's <c>default</c>, in its data directory
    /// (the folder of its key file), not in the folder it was started in (#282).
    /// </summary>
    [Fact]
    public async Task WithNoRoots_TheFallbackRootIsInTheDataDirectory_NotTheWorkingDirectory()
    {
        var workDir = ServerProcess.CreateWorkDir("fallback-root-cwd");
        var dataDir = ServerProcess.CreateWorkDir("fallback-root-data");
        try
        {
            var fallbackRoot = Path.Combine(dataDir, ServerDataDirectory.FallbackRootName);
            Directory.CreateDirectory(Path.Combine(fallbackRoot, "existing"));

            using var server = ServerProcess.Start(workDir, "http://127.0.0.1:0",
                arguments: [$"--{ApiKeyFile.PathSetting}={Path.Combine(dataDir, ApiKeyFile.FileName)}"]);
            var url = await StartAsync(server);

            Assert.Contains(await ListRootsAsync(url), root => (root.ProfileName, root.Name) == ("Default", "default"));
            await using var client = new ServerHubClient(url);
            await client.StartAsync();
            var unmanaged = await client.Hub.InvokeAsync<UnmanagedFolder[]>(nameof(IProjectHub.ListUnmanaged), "Default", "default");
            Assert.Contains(unmanaged, folder => folder.Name == "existing");
            Assert.Contains($"Default/default={fallbackRoot}", server.Output);
            Assert.False(Directory.Exists(Path.Combine(workDir, ServerDataDirectory.FallbackRootName)));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
            ServerProcess.DeleteWorkDir(dataDir);
        }
    }

    /// <summary>
    /// Two server processes on the same roots:the second lists none of the first one's, and logs
    /// them with its instance. Killed hard, the first one's lock goes with it, and the second one
    /// picks the roots up on its next rebuild.
    /// </summary>
    [Fact]
    public async Task SecondServer_LeavesTheFirstOnesRoots_UntilTheFirstIsKilled()
    {
        var mainDir = ServerProcess.CreateWorkDir("instance-main");
        var devDir = ServerProcess.CreateWorkDir("instance-dev");
        try
        {
            WriteRoot(Path.Combine(mainDir, "roots"), "shared", "p");

            using var main = ServerProcess.Start(mainDir, "http://127.0.0.1:0", arguments: ["--Instance=main"]);
            Assert.Contains(await ListRootsAsync(await StartAsync(main)), root => root.Name == "shared");

            using var dev = ServerProcess.Start(devDir, "http://127.0.0.1:0", rootsOnCommandLine: false,
                arguments: ["--Instance=dev", $"--Roots:Scan:default={Path.Combine(mainDir, "roots")}"]);
            var devUrl = await StartAsync(dev);

            Assert.DoesNotContain(await ListRootsAsync(devUrl), root => root.Name == "shared");
            Assert.Contains($"held by another server (instance main, process {main.ProcessId})", dev.Output);

            main.Dispose();

            Assert.Contains(await ListRootsAsync(devUrl), root => root.Name == "shared");
        }
        finally
        {
            ServerProcess.DeleteWorkDir(mainDir);
            ServerProcess.DeleteWorkDir(devDir);
        }
    }
}
