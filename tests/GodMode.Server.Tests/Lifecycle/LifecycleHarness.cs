using System.Diagnostics;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Server.Hubs;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// The real <see cref="ProjectManager"/> and <see cref="ClaudeProcessManager"/>, wired as Program.cs
/// wires them, launching <c>GodMode.FakeClaude</c> instead of <c>claude</c>. Each harness owns a temp
/// roots dir with one root, <see cref="RootName"/>, whose minimal <c>.godmode-root/config.json</c>
/// names its profile and tells the fake where its script and sidecar are. Every project launched from it plays the
/// script last passed to <see cref="UseScript"/> and records to <c>fake-claude.jsonl</c> in its own folder.
/// </summary>
internal sealed class LifecycleHarness : IAsyncDisposable
{
    public const string RootName = "lifecycle";
    public const string ProfileName = "lifecycle";

    /// <summary>Long enough for a slow CI box; each wait returns as soon as its condition holds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string RecordFileName = "fake-claude.jsonl";

    private readonly string _workDir;
    private readonly ServiceProvider _services;
    private readonly List<string> _projectIds = [];
    private readonly CapturingLoggerProvider _logs = new();

    public string RootPath { get; }
    public string ScriptPath { get; }
    public IProjectManager Projects { get; }

    /// <summary>Every push the server makes to its hub clients.</summary>
    public RecordingHubContext Hub { get; } = new();

    /// <summary>The fake's apphost next to the test assembly (copied there by the project reference).</summary>
    public static string FakeClaudePath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "GodMode.FakeClaude.exe" : "GodMode.FakeClaude");

    /// <param name="script">The script every launch plays until <see cref="UseScript"/> replaces it.</param>
    /// <param name="rootConfig">Extra top-level properties for the root's config.json (for example <c>claudeArgs</c>).</param>
    /// <param name="settings">Extra server configuration, applied over the harness defaults.</param>
    public LifecycleHarness(
        FakeScript script,
        IReadOnlyDictionary<string, object>? rootConfig = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _workDir = ServerProcess.CreateWorkDir("lifecycle");
        var rootsDir = Path.Combine(_workDir, "roots");
        RootPath = Path.Combine(rootsDir, RootName);
        ScriptPath = Path.Combine(_workDir, "fake-claude.script");
        UseScript(script);
        WriteRootConfig(rootConfig);

        var configuration = new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = rootsDir,
            [ClaudeProcessManager.ExecutableSetting] = FakeClaudePath,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            configuration[key] = value;

        _services = BuildServices(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(), _logs, Hub);
        Projects = _services.GetRequiredService<IProjectManager>();
    }

    /// <summary>Replaces the script that the next launch plays. Running fakes keep the one they loaded.</summary>
    public void UseScript(FakeScript script) => script.Save(ScriptPath);

    private void WriteRootConfig(IReadOnlyDictionary<string, object>? extra)
    {
        var config = new Dictionary<string, object>
        {
            ["profileName"] = ProfileName,
            ["environment"] = new Dictionary<string, string>
            {
                [FakeClaudeEnvironment.Script] = ScriptPath,
                [FakeClaudeEnvironment.Record] = RecordFileName,
            },
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object>())
            config[key] = value;

        var godModeRoot = Path.Combine(RootPath, ".godmode-root");
        Directory.CreateDirectory(godModeRoot);
        File.WriteAllText(Path.Combine(godModeRoot, "config.json"), JsonSerializer.Serialize(config));
    }

    private static ServiceProvider BuildServices(IConfiguration configuration, ILoggerProvider logs, RecordingHubContext hub)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddSignalR();
        services.AddSingleton<IHubContext<ProjectHub, IProjectHubClient>>(hub);
        services.AddSingleton(configuration);
        services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
        services.AddSingleton<IStatusUpdater, StatusUpdater>();
        services.AddSingleton<ProjectLifecycle>();
        services.AddSingleton<IRootConfigReader, RootConfigReader>();
        services.AddSingleton<IScriptRunner, ScriptRunner>();
        services.AddSingleton<ProfileFileManager>();
        services.AddSingleton<IHostApplicationLifetime, ApplicationLifetime>();
        services.AddSingleton<IProjectManager, ProjectManager>();
        return services.BuildServiceProvider();
    }

    // ── Projects ──

    /// <summary>Creates a project in the harness root (the default "Create" action) and returns its status.</summary>
    public async Task<ProjectStatus> CreateProjectAsync(string name = "p1", string prompt = "Say hello")
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["prompt"] = JsonSerializer.SerializeToElement(prompt),
        };
        var status = await Projects.CreateProjectAsync(new CreateProjectRequest(ProfileName, RootName, inputs));
        _projectIds.Add(status.Id);
        return status;
    }

    public string ProjectPath(string projectId) => Path.Combine(RootPath, projectId);

    public IClaudeProcessManager ProcessManager => _services.GetRequiredService<IClaudeProcessManager>();

    /// <summary>Stops the host as the server's does on shutdown: raises ApplicationStopping and waits for its handlers.</summary>
    public void StopHost() => ((ApplicationLifetime)_services.GetRequiredService<IHostApplicationLifetime>()).StopApplication();

    /// <summary>
    /// Runs <paramref name="callback"/> when the host stops, before the server's own handlers
    /// (ApplicationStopping runs the latest registration first).
    /// </summary>
    public void OnHostStopping(Action callback) =>
        _services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(callback);

    /// <summary>The server's own record of a project, found as the MCP bridge finds it: by its latest launch's token.</summary>
    public ProjectInfo ProjectInfo(string projectId) =>
        Projects.ValidateProjectToken(projectId, Launches(projectId)[^1].Environment["GODMODE_PROJECT_TOKEN"])
        ?? throw new InvalidOperationException($"project {projectId} does not accept its launch's token");

    /// <summary>Polls the in-memory status until it reaches <paramref name="state"/>.</summary>
    public async Task<ProjectStatus> WaitForStateAsync(string projectId, ProjectState state, TimeSpan? timeout = null)
    {
        ProjectStatus status = await Projects.GetStatusAsync(projectId);
        await WaitUntilAsync(async () => (status = await Projects.GetStatusAsync(projectId)).State == state, timeout,
            () => $"project {projectId} did not reach {state}; it is {status.State}.\n{Describe(projectId)}");
        return status;
    }

    /// <summary>
    /// Waits for a <c>StatusChanged</c> push for the project that satisfies <paramref name="condition"/>,
    /// among those after the first <paramref name="skip"/>.
    /// </summary>
    public async Task<ProjectStatus> WaitForStatusPushAsync(string projectId, Func<ProjectStatus, bool> condition,
        int skip = 0, TimeSpan? timeout = null)
    {
        ProjectStatus? pushed = null;
        await WaitUntilAsync(() => Task.FromResult((pushed = Hub.StatusPushes(projectId).Skip(skip).LastOrDefault(condition)) != null),
            timeout,
            () => $"no matching StatusChanged was pushed for project {projectId}; pushed: " +
                  $"{string.Join(", ", Hub.StatusPushes(projectId).Select(s => s.State))}.\n{Describe(projectId)}");
        return pushed!;
    }

    /// <summary>
    /// status.json as it is on disk, which is what recovery and a restarted server read. The server
    /// rewrites it in place, so a read can meet a locked or half-written file; those are retried.
    /// </summary>
    public ProjectStatus ReadStatusFile(string projectId)
    {
        var path = Path.Combine(ProjectPath(projectId), ".godmode", "status.json");
        for (var attempt = 1; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<ProjectStatus>(ReadShared(path), JsonDefaults.Options)!; }
            catch (Exception ex) when (ex is IOException or JsonException && attempt < 50) { Thread.Sleep(20); }
        }
    }

    public string ReadOutputFile(string projectId) =>
        ReadShared(Path.Combine(ProjectPath(projectId), ".godmode", "output.jsonl"));

    /// <summary>Reads a file the server may still hold open for writing (output.jsonl, errs.txt, status.json).</summary>
    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    // ── What the fake saw ──

    public IReadOnlyList<FakeLaunch> Launches(string projectId) =>
        FakeRecording.Read(Path.Combine(ProjectPath(projectId), RecordFileName));

    /// <summary>Waits until the project's launch number <paramref name="index"/> satisfies <paramref name="condition"/>.</summary>
    public async Task<FakeLaunch> WaitForLaunchAsync(string projectId, Func<FakeLaunch, bool> condition, int index = 0,
        TimeSpan? timeout = null)
    {
        FakeLaunch? launch = null;
        await WaitUntilAsync(() =>
            {
                var launches = Launches(projectId);
                launch = launches.Count > index ? launches[index] : null;
                return Task.FromResult(launch != null && condition(launch));
            }, timeout,
            () => $"launch {index} of project {projectId} did not reach the expected condition.\n{Describe(projectId)}");
        return launch!;
    }

    /// <summary>Waits for the launch to have received <paramref name="count"/> stdin lines.</summary>
    public Task<FakeLaunch> WaitForStdinAsync(string projectId, int count = 1, int index = 0) =>
        WaitForLaunchAsync(projectId, l => l.Stdin.Count >= count, index);

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds (true) or the timeout passes (false).</summary>
    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20);
        }
        return true;
    }

    /// <summary>As <see cref="WaitForAsync"/>, failing the test with <paramref name="failure"/> on timeout.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout, Func<string> failure)
    {
        if (!await WaitForAsync(condition, timeout))
            Assert.Fail(failure());
    }

    /// <summary>Everything a failed wait needs to be diagnosed from the test output alone.</summary>
    public string Describe(string projectId)
    {
        var godMode = Path.Combine(ProjectPath(projectId), ".godmode");
        string Read(string file) => File.Exists(Path.Combine(godMode, file)) ? ReadShared(Path.Combine(godMode, file)) : "(none)";
        var launches = Launches(projectId);
        return $"""
            status.json: {Read("status.json")}
            output.jsonl:
            {Read("output.jsonl")}
            errs.txt:
            {Read("errs.txt")}
            fake launches: {launches.Count}
            {string.Join("\n", launches.Select(l => $"  pid {l.Pid}, stdin lines {l.Stdin.Count}, exit {l.ExitCode?.ToString() ?? "(none)"}"))}
            server warnings and errors (all projects):
            {string.Join("\n", _logs.Lines)}
            """;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _projectIds)
        {
            try { await Projects.StopProjectAsync(id); }
            catch (Exception) { /* best effort: the test may have stopped or broken it already */ }
        }
        await _services.DisposeAsync();
        ServerProcess.DeleteWorkDir(_workDir);
    }
}
