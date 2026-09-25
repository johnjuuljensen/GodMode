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
/// names its profile and tells the fake where its script and sidecar are, and any extra roots asked for,
/// configured the same way. Every project launched from them plays the script last passed to
/// <see cref="UseScript"/> and records to <c>fake-claude.jsonl</c> in its own folder.
/// </summary>
internal sealed class LifecycleHarness : IAsyncDisposable
{
    public const string RootName = "lifecycle";
    public const string ProfileName = "lifecycle";

    /// <summary>Long enough for a slow CI box; each wait returns as soon as its condition holds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string RecordFileName = "fake-claude.jsonl";

    private readonly string _workDir;
    private ServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly List<ServiceProvider> _stopped = [];
    private readonly List<string> _projectIds = [];
    private readonly CapturingLoggerProvider _logs = new();

    /// <summary>The temp dir everything the harness and the server write lives under.</summary>
    public string WorkDir => _workDir;

    /// <summary>The server's <c>ProjectRootsDir</c>: one subdirectory per root.</summary>
    public string RootsDir { get; }

    public string RootPath { get; }
    public string ScriptPath { get; }
    public IProjectManager Projects { get; private set; }

    /// <summary>The server's warnings and errors so far, across restarts.</summary>
    public IReadOnlyCollection<string> Warnings => _logs.Lines;

    /// <summary>Every push the server makes to its hub clients (since the last <see cref="RestartAsync"/>).</summary>
    public RecordingHubContext Hub { get; private set; } = new();

    /// <summary>The fake's apphost next to the test assembly (copied there by the project reference).</summary>
    public static string FakeClaudePath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "GodMode.FakeClaude.exe" : "GodMode.FakeClaude");

    /// <param name="script">The script every launch plays until <see cref="UseScript"/> replaces it.</param>
    /// <param name="rootConfig">Extra top-level properties for the root's config.json (for example <c>claudeArgs</c>).</param>
    /// <param name="settings">Extra server configuration, applied over the harness defaults.</param>
    /// <param name="extraRoots">More roots beside <see cref="RootName"/>, each in the profile given, configured as it is.</param>
    /// <param name="profileEnvironment">The environment of <see cref="ProfileName"/>, in its <c>.profiles/</c> env.json.</param>
    public LifecycleHarness(
        FakeScript script,
        IReadOnlyDictionary<string, object>? rootConfig = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        IReadOnlyList<(string Root, string Profile)>? extraRoots = null,
        IReadOnlyDictionary<string, string>? profileEnvironment = null)
    {
        _workDir = ServerProcess.CreateWorkDir("lifecycle");
        RootsDir = Path.Combine(_workDir, "roots");
        RootPath = Path.Combine(RootsDir, RootName);
        ScriptPath = Path.Combine(_workDir, "fake-claude.script");
        UseScript(script);
        WriteRootConfig(RootPath, ProfileName, rootConfig);
        foreach (var (root, profile) in extraRoots ?? [])
            WriteRootConfig(Path.Combine(RootsDir, root), profile, rootConfig);
        if (profileEnvironment != null)
        {
            var profileDir = Path.Combine(RootsDir, ".profiles", ProfileName);
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(Path.Combine(profileDir, "env.json"), JsonSerializer.Serialize(profileEnvironment));
        }

        var configuration = new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = RootsDir,
            [ClaudeProcessManager.ExecutableSetting] = FakeClaudePath,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            configuration[key] = value;

        _configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();
        _services = BuildServices(_configuration, _logs, Hub);
        Projects = _services.GetRequiredService<IProjectManager>();
    }

    /// <summary>
    /// Restarts the server over the same roots: the host stops (every project is stopped and
    /// persisted), then a new server, with a new hub, recovers the projects from their files and
    /// carries on with those the shutdown interrupted, as Program.cs does once the server is started.
    /// </summary>
    /// <param name="resume">False to leave carrying on to the test, which calls <see cref="IProjectManager.ResumeInterruptedProjectsAsync"/>.</param>
    public async Task RestartAsync(bool resume = true)
    {
        StopHost();
        _stopped.Add(_services);
        Hub = new RecordingHubContext();
        _services = BuildServices(_configuration, _logs, Hub);
        Projects = _services.GetRequiredService<IProjectManager>();
        await Projects.RecoverProjectsAsync();
        if (resume) await Projects.ResumeInterruptedProjectsAsync();
    }

    /// <summary>Replaces the script that the next launch plays. Running fakes keep the one they loaded.</summary>
    public void UseScript(FakeScript script) => script.Save(ScriptPath);

    private void WriteRootConfig(string rootPath, string profileName, IReadOnlyDictionary<string, object>? extra)
    {
        var config = new Dictionary<string, object>
        {
            ["profileName"] = profileName,
            ["environment"] = new Dictionary<string, string>
            {
                [FakeClaudeEnvironment.Script] = ScriptPath,
                [FakeClaudeEnvironment.Record] = RecordFileName,
            },
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object>())
            config[key] = value;

        var godModeRoot = Path.Combine(rootPath, ".godmode-root");
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
        services.AddSingleton<ClaudeProcessManager>();
        services.AddSingleton<HoldingProcessManager>();
        services.AddSingleton<IClaudeProcessManager>(provider => provider.GetRequiredService<HoldingProcessManager>());
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

    /// <summary>
    /// Creates a project (the default "Create" action) in the harness root, or in
    /// <paramref name="root"/> of <paramref name="profile"/>, and returns its status.
    /// </summary>
    public async Task<ProjectStatus> CreateProjectAsync(string name = "p1", string prompt = "Say hello",
        string root = RootName, string profile = ProfileName, IReadOnlyDictionary<string, object>? inputs = null)
    {
        var request = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["prompt"] = JsonSerializer.SerializeToElement(prompt),
        };
        foreach (var (key, value) in inputs ?? new Dictionary<string, object>())
            request[key] = JsonSerializer.SerializeToElement(value);
        var status = await Projects.CreateProjectAsync(new CreateProjectRequest(profile, root, request));
        _projectIds.Add(status.Id);
        return status;
    }

    /// <summary>
    /// The folder of a project: <c>{profile}/{root}/{folder}</c> is found in its root's directory
    /// (a discovered root's name is its directory's); a bare folder name in <see cref="RootPath"/>.
    /// </summary>
    public string ProjectPath(string projectId) =>
        projectId.Split('/') is [.., var root, var folder] ? Path.Combine(RootsDir, root, folder) : Path.Combine(RootPath, projectId);

    public IClaudeProcessManager ProcessManager => _services.GetRequiredService<IClaudeProcessManager>();

    /// <summary>
    /// Holds the next launch (a create's or a resume's) after its claim, before its process is
    /// started, until <see cref="LaunchHold.Release"/>.
    /// </summary>
    public LaunchHold HoldNextLaunch() => _services.GetRequiredService<HoldingProcessManager>().HoldNext();

    /// <summary>Opens a client connection to the hub.</summary>
    public HarnessConnection Connect(string connectionId) =>
        new(connectionId, Hub, Projects, _services.GetRequiredService<ILogger<ProjectHub>>());

    /// <summary>Stops the host as the server's does on shutdown: raises ApplicationStopping and waits for its handlers.</summary>
    public void StopHost() => ((ApplicationLifetime)_services.GetRequiredService<IHostApplicationLifetime>()).StopApplication();

    /// <summary>
    /// Runs <paramref name="callback"/> when the host stops, before the server's own handlers
    /// (ApplicationStopping runs the latest registration first).
    /// </summary>
    public void OnHostStopping(Action callback) =>
        _services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(callback);

    /// <summary>The server's own record of a project, found as the MCP endpoint finds it: by the token in its latest launch's MCP config.</summary>
    public ProjectInfo ProjectInfo(string projectId) =>
        Projects.ValidateProjectToken(projectId, GodModeMcpEntry.Of(Launches(projectId)[^1]).Token)
        ?? throw new InvalidOperationException($"project {projectId} does not accept its launch's token");

    /// <summary>The server's own record of a project, whether or not a launch of it has recorded anything.</summary>
    public ProjectInfo Tracked(string projectId) =>
        ((ProjectManager)Projects).Tracked(projectId) ?? throw new InvalidOperationException($"project {projectId} is not tracked");

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

    /// <summary>The real <see cref="ClaudeProcessManager"/>, whose next launch a test can hold before it starts its process.</summary>
    private sealed class HoldingProcessManager(ClaudeProcessManager inner) : IClaudeProcessManager
    {
        private LaunchHold? _next;

        public LaunchHold HoldNext() => _next = new LaunchHold();

        private async Task PassAsync()
        {
            if (Interlocked.Exchange(ref _next, null) is { } hold) await hold.WaitAsync();
        }

        public async Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null)
        {
            await PassAsync();
            return await inner.StartClaudeProcessAsync(project, initialPrompt, cancellationToken, extraEnvironment, extraArgs);
        }

        public async Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken,
            Dictionary<string, string>? extraEnvironment = null, string[]? extraArgs = null)
        {
            await PassAsync();
            return await inner.ResumeClaudeProcessAsync(project, cancellationToken, extraEnvironment, extraArgs);
        }

        public Task SendInputAsync(ProjectInfo project, string input) => inner.SendInputAsync(project, input);
        public Task StopProcessAsync(ProjectInfo project, TimeSpan? grace = null) => inner.StopProcessAsync(project, grace);
        public Task SettleAsync(ProjectInfo project) => inner.SettleAsync(project);
        public TimeSpan StopGracePeriod => inner.StopGracePeriod;
        public bool IsProcessRunning(int processId) => inner.IsProcessRunning(processId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _projectIds)
        {
            // Bounded: a project that cannot be stopped fails its test, not the whole run
            try { await Projects.StopProjectAsync(id).WaitAsync(DefaultTimeout); }
            catch (Exception) { /* best effort: the test may have stopped or broken it already */ }
        }
        await _services.DisposeAsync();
        foreach (var stopped in _stopped) await stopped.DisposeAsync();
        ServerProcess.DeleteWorkDir(_workDir);
    }
}

/// <summary>A launch held before its process starts: see <see cref="LifecycleHarness.HoldNextLaunch"/>.</summary>
internal sealed class LaunchHold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the launch has come to the hold.</summary>
    public Task Reached => _reached.Task;

    public void Release() => _released.TrySetResult();

    internal Task WaitAsync()
    {
        _reached.TrySetResult();
        return _released.Task;
    }
}
