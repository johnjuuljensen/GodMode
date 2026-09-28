using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using SignalR.Proxy;

namespace GodMode.Voice.Tests;

/// <summary>
/// The real GodMode.Server, as a process in a work directory of its own, with one root whose projects run
/// GodMode.FakeClaude instead of claude (as GodMode.Server.Tests' ServerProcess and LifecycleHarness do). Every
/// launch plays the script last given to <see cref="UseScript"/> and records to <c>fake-claude.jsonl</c> in its project.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string ApiKey = "voice-test-api-key-0123456789abcdef";
    public const string Profile = "voice";
    public const string Root = "voice";
    private const string RecordFileName = "fake-claude.jsonl";

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly string _workDir;
    private readonly string _scriptPath;

    public string Url { get; }
    public string RootPath { get; }

    private TestServer(string workDir, int port)
    {
        _workDir = workDir;
        Url = $"http://127.0.0.1:{port}";
        RootPath = Path.Combine(workDir, "roots", Root);
        _scriptPath = Path.Combine(workDir, "fake-claude.script");

        var godModeRoot = Path.Combine(RootPath, ".godmode-root");
        System.IO.Directory.CreateDirectory(godModeRoot);
        File.WriteAllText(Path.Combine(godModeRoot, "config.json"), JsonSerializer.Serialize(new
        {
            profileName = Profile,
            environment = new Dictionary<string, string>
            {
                [FakeClaudeEnvironment.Script] = _scriptPath,
                [FakeClaudeEnvironment.Record] = RecordFileName,
            },
        }));

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "GodMode.Server.dll"));
        psi.ArgumentList.Add($"--ProjectRootsDir={Path.Combine(workDir, "roots")}");
        psi.ArgumentList.Add($"--Urls={Url}");
        psi.ArgumentList.Add($"--Authentication:ApiKey={ApiKey}");
        psi.ArgumentList.Add($"--Authentication:ApiKeyFile={Path.Combine(workDir, "data", "api-key")}");
        psi.ArgumentList.Add($"--Claude:Executable={FakeClaudePath}");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ASPNETCORE_URLS"] = "";
        psi.Environment["CODESPACES"] = "";
        psi.Environment["GITHUB_USER"] = "";
        psi.Environment.Remove("Authentication__ApiKey");

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
    }

    public string Output { get { lock (_output) return _output.ToString(); } }

    private static string FakeClaudePath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "GodMode.FakeClaude.exe" : "GodMode.FakeClaude");

    public static async Task<TestServer> StartAsync(FakeScript script)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"godmode-voice-e2e-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Path.Combine(workDir, "roots"));
        var server = new TestServer(workDir, FreePort());
        server.UseScript(script);
        server._process.Start();
        server._process.BeginOutputReadLine();
        server._process.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        await Eventually.UntilAsync(() =>
        {
            if (server._process.HasExited) Assert.Fail($"The server exited.\n{server.Output}");
            try { return http.GetAsync("/health").Result.IsSuccessStatusCode; }
            catch (AggregateException) { return false; }
        }, () => $"the server to be healthy.\n{server.Output}", TimeSpan.FromSeconds(60));
        return server;
    }

    /// <summary>The script the next launch plays.</summary>
    public void UseScript(FakeScript script) => script.Save(_scriptPath);

    /// <summary>A directory with this server alone in it, reached with its key, as the app's registry gives it.</summary>
    public IServerDirectory ServerDirectory(string serverId = "local") => new OneServer(serverId, new RelayTarget($"{Url}/hubs/projects", ApiKey));

    /// <summary>What the fake claude of a project read from its stdin, over all its launches.</summary>
    public IReadOnlyList<string> StdinOf(string projectId)
    {
        var folder = projectId.Split('/')[^1];
        return [.. FakeRecording.Read(Path.Combine(RootPath, folder, RecordFileName)).SelectMany(l => l.Stdin)];
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
        try { System.IO.Directory.Delete(_workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class OneServer(string id, RelayTarget target) : IServerDirectory
    {
        private readonly ServerInfo _info = new(id, "Test server", ServerTypes.Local, ServerState.Running);

        public Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServerInfo>>([_info]);
        public Task<IReadOnlyList<RegistrationListing>> ListByRegistrationAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RegistrationListing>>([new RegistrationListing("registration", [_info])]);
        public Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(serverId == id ? target : null);
        public Task<bool> StartServerAsync(string serverId) => Task.FromResult(false);
        public Task<bool> StopServerAsync(string serverId) => Task.FromResult(false);
        public Task<bool> RemoveServerAsync(string serverId) => Task.FromResult(false);
    }
}
