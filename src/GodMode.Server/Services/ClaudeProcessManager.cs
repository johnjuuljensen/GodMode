using GodMode.Server.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Manages Claude Code processes using Process directly for proper stdin handling.
/// </summary>
public class ClaudeProcessManager : IClaudeProcessManager
{
    private static readonly string[] DefaultArgs =
    [
        "--print",
        "--replay-user-messages",
        "--verbose",
        "--output-format=stream-json",
        "--input-format=stream-json"
    ];

    /// <summary>Configuration key for the Claude Code executable (a name on PATH or a full path).</summary>
    public const string ExecutableSetting = "Claude:Executable";

    /// <summary>What claude writes to stderr, then exits, when <c>--resume</c> names a session it has no conversation for.</summary>
    private const string NoConversationError = "No conversation found with session ID:";

    /// <summary>How many of its last stderr lines an exit carries.</summary>
    private const int StderrTailLines = 20;

    /// <summary>
    /// How long an exited process's pipes may stay open. A grandchild that inherited them can hold
    /// them past the exit; the exit is handled without its remaining output then.
    /// </summary>
    private static readonly TimeSpan PipeDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Configuration key: how long a stop gives claude, once interrupted, to exit before its whole
    /// process tree is killed. Seconds; 10 by default.
    /// </summary>
    public const string StopGracePeriodSetting = "StopGracePeriodSeconds";

    /// <summary>How long a stop waits for a send in progress before it closes claude's input anyway.</summary>
    private static readonly TimeSpan StdinCloseWait = TimeSpan.FromSeconds(1);

    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly string _executable;
    private readonly ConcurrentDictionary<string, Launch> _processes = new();

    public ClaudeProcessManager(ILogger<ClaudeProcessManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        _executable = configuration[ExecutableSetting] is { Length: > 0 } executable ? executable : "claude";
        StopGracePeriod = TimeSpan.FromSeconds(configuration.GetValue(StopGracePeriodSetting, 10.0));
    }

    public TimeSpan StopGracePeriod { get; }

    /// <summary>One claude process, from its start until its exit has been handed to the pipeline.</summary>
    private sealed class Launch(Process process, SessionProcessTree tree)
    {
        public Process Process { get; } = process;

        /// <summary>The process and everything it starts: what a stop interrupts, then kills.</summary>
        public SessionProcessTree Tree { get; } = tree;

        public int Id { get; set; }

        /// <summary>Completes when the process has exited, before its pipes are drained and its exit handled.</summary>
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the exit is on the pipeline (or a fallback launch replaced it).</summary>
        public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _stopped;

        /// <summary>The server is stopping it: its exit is not a failure, no launch takes its place, and it takes no more input.</summary>
        public bool Stopped => _stopped;

        public void MarkStopped() => _stopped = true;
    }

    /// <summary>
    /// Decides, once a launch has exited and been drained, whether another launch takes its place.
    /// True means one did, and this exit is not reported.
    /// </summary>
    private delegate Task<bool> ExitTakeover(int exitCode, IReadOnlyCollection<string> stderrTail);

    public async Task<int> StartClaudeProcessAsync(
        ProjectInfo project,
        string initialPrompt,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment = null,
        string[]? extraArgs = null)
    {
        _logger.LogInformation("Starting Claude process for project {ProjectId}", project.Status.Id);

        var sessionId = project.SessionId ?? Guid.NewGuid().ToString();
        project.SessionId = sessionId;

        // The ID asked for; claude's system/init replaces it if claude keeps another
        await SessionIdFile.WriteAsync(project.ProjectPath, sessionId, cancellationToken);

        // Start with session ID, send prompt via stdin
        var args = BuildArgs(["--session-id", sessionId], extraArgs);
        return await RunClaudeProcessAsync(project, args, initialPrompt, cancellationToken, extraEnvironment);
    }

    public async Task<int> ResumeClaudeProcessAsync(
        ProjectInfo project,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment = null,
        string[]? extraArgs = null)
    {
        // No session to resume (none saved, or one that was no GUID): a fresh one takes its place,
        // as it does when claude has no conversation for the session
        if (project.SessionId is not { } sessionId)
        {
            _logger.LogWarning("Project {ProjectId} has no session to resume. Starting fresh session.", project.Status.Id);
            project.SessionId = Guid.NewGuid().ToString();
            return await StartFreshSessionAsync(project, cancellationToken, extraEnvironment, extraArgs);
        }

        _logger.LogInformation("Resuming Claude process for project {ProjectId} with session {SessionId}",
            project.Status.Id, sessionId);

        var args = BuildArgs(["--resume", sessionId], extraArgs);

        // claude has no conversation for the session: it exits at once, and a fresh session on the
        // same id takes its place. That launch uses the MCP config the resume was given, which is
        // kept for it and deleted after its own exit.
        return await RunClaudeProcessAsync(project, args, null, cancellationToken, extraEnvironment,
            async (exitCode, stderrTail) =>
            {
                if (exitCode == 0 || !stderrTail.Any(line => line.Contains(NoConversationError))) return false;

                _logger.LogWarning("Resume failed for project {ProjectId}, session {SessionId} not found. Starting fresh session.",
                    project.Status.Id, project.SessionId);
                try
                {
                    await StartFreshSessionAsync(project, cancellationToken, extraEnvironment, extraArgs);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not start a fresh session for project {ProjectId}", project.Status.Id);
                    return false;
                }
            });
    }

    /// <summary>A new session on the project's session ID, told to carry on from the work in its folder.</summary>
    private async Task<int> StartFreshSessionAsync(ProjectInfo project, CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment, string[]? extraArgs)
    {
        await SessionIdFile.WriteAsync(project.ProjectPath, project.SessionId!, cancellationToken);
        return await RunClaudeProcessAsync(project, BuildArgs(["--session-id", project.SessionId!], extraArgs),
            "Continue from where we left off. Review the codebase and previous work.", cancellationToken, extraEnvironment);
    }

    private static string[] BuildArgs(string[] additionalArgs, string[]? extraArgs = null)
    {
        var result = new List<string>(DefaultArgs);
        result.AddRange(additionalArgs);
        if (extraArgs != null)
            result.AddRange(extraArgs);
        return result.ToArray();
    }

    private async Task<int> RunClaudeProcessAsync(
        ProjectInfo project,
        string[] args,
        string? initialPrompt,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment,
        ExitTakeover? takeover = null)
    {
        var godModePath = Path.Combine(project.ProjectPath, ".godmode");
        var output = project.Process.Output;
        var stderrPath = Path.Combine(godModePath, "errs.txt");

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = project.ProjectPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Start from the allowlist, not the server's environment, then the configured variables
        startInfo.Environment.Clear();
        foreach (var (key, value) in ChildEnvironment.Claude.Build(ChildEnvironment.Current(), extraEnvironment))
            startInfo.Environment[key] = value;

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Its own tree, off the server's console: what a stop interrupts, then kills whole
        var tree = SessionProcessTree.Create(_logger);
        try
        {
            tree.Prepare(startInfo);
        }
        catch
        {
            tree.Dispose();
            throw;
        }

        // stdout goes to the project's output pipeline, whose consumer writes output.jsonl
        var stderrStream = new FileStream(stderrPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var stderrWriter = new StreamWriter(stderrStream, Encoding.UTF8) { AutoFlush = true };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var launch = new Launch(process, tree);

        // The exit is handled once the process has exited and both pipes are drained, so it
        // follows every line the process wrote
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTail = new ConcurrentQueue<string>();

        process.Exited += (_, _) => launch.Exited.TrySetResult();

        // stdout: only hand the line on. The project's one consumer writes it to output.jsonl, updates
        // state and broadcasts it, in order; doing that here raced the lines of one burst.
        void OnStdout(string? line)
        {
            if (line == null)
                stdoutClosed.TrySetResult();
            else if (!output.TryWrite(new PipelineItem.Line(line)))
                _logger.LogDebug("Dropped output for project {ProjectId}: its pipeline is closed", project.Status.Id);
        }

        void OnStderr(string? line)
        {
            if (line == null)
            {
                stderrWriter.Dispose();
                stderrClosed.TrySetResult();
                return;
            }

            try
            {
                stderrWriter.WriteLine($"[{DateTime.UtcNow:O}] {line}");
                _logger.LogWarning("Claude stderr [{ProjectId}]: {Error}", project.Status.Id, line);

                stderrTail.Enqueue(line);
                while (stderrTail.Count > StderrTailLines) stderrTail.TryDequeue(out string? _);

                // Show error lines in the UI as synthetic error output events, through the same
                // pipeline so they persist to output.jsonl for backfill on refresh. They do not
                // change the project's state: the exit and error results do.
                if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    output.TryWrite(new PipelineItem.Line(JsonSerializer.Serialize(new { type = "error", error = line })));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing stderr for project {ProjectId}", project.Status.Id);
            }
        }

        _logger.LogInformation("Starting Claude process for project {ProjectId} with args: {Args}",
            project.Status.Id, string.Join(" ", args));

        var launchedAt = DateTime.UtcNow;
        try
        {
            // A stop came first (it cancels the launch): nothing is started
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
        }
        catch
        {
            // Never started: there is no exit to handle
            stderrWriter.Dispose();
            process.Dispose();
            tree.Dispose();
            throw;
        }

        try
        {
            tree.Attach(process);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude process {ProcessId} for project {ProjectId} is not in a process tree of its own: a stop kills it and the children it still has",
                process.Id, project.Status.Id);
        }

        launch.Id = process.Id;
        _processes[project.Status.Id] = launch;
        project.Process.ProcessId = launch.Id;
        // On threads of their own: a session holds no thread-pool thread for its lifetime
        _ = ChildOutput.ReadLinesAsync(process.StandardOutput, OnStdout, $"claude {launch.Id} stdout");
        _ = ChildOutput.ReadLinesAsync(process.StandardError, OnStderr, $"claude {launch.Id} stderr");

        _logger.LogInformation("Claude process started for project {ProjectId} with PID {ProcessId}",
            project.Status.Id, launch.Id);

        // A stop cancels the launch: this process is being stopped, and no fresh session takes its place
        var cancellation = cancellationToken.Register(launch.MarkStopped);

        _ = HandleExitAsync(project, launch, launchedAt, Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
            stderrTail, takeover, cancellation);

        // Send initial prompt via stdin if provided
        if (!string.IsNullOrEmpty(initialPrompt))
        {
            await SendInputAsync(project, initialPrompt);
        }

        return launch.Id;
    }

    /// <summary>
    /// After the process exits and its pipes drain: lets <paramref name="takeover"/> replace it, or
    /// else deletes its MCP config, clears its PID and puts its exit on the pipeline, last. What is
    /// left of its tree goes with it.
    /// </summary>
    private async Task HandleExitAsync(ProjectInfo project, Launch launch, DateTime launchedAt, Task drained,
        ConcurrentQueue<string> stderrTail, ExitTakeover? takeover, CancellationTokenRegistration cancellation)
    {
        var id = project.Status.Id;
        try
        {
            await launch.Exited.Task;
            if (await Task.WhenAny(drained, Task.Delay(PipeDrainTimeout)) != drained)
                _logger.LogWarning("Claude process {ProcessId} for project {ProjectId} exited, but its output is still open; handling the exit without it",
                    launch.Id, id);
            await cancellation.DisposeAsync();

            var exitCode = launch.Process.ExitCode;
            var tail = stderrTail.ToArray();
            _logger.LogInformation("Claude process exited for project {ProjectId} with exit code {ExitCode} (PID {ProcessId}{Stopped})",
                id, exitCode, launch.Id, launch.Stopped ? ", stopped" : "");

            if (!launch.Stopped && takeover != null && await takeover(exitCode, tail))
                return;

            try { McpConfigFile.DeleteIfWrittenBefore(project.ProjectPath, launchedAt); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the MCP config for project {ProjectId}", id); }

            project.Process.ClearProcessId(launch.Id);
            var stderr = tail.Length > 0 ? string.Join("\n", tail) : null;
            if (!project.Process.Output.TryWrite(new PipelineItem.Exited(new ProcessExit(launch.Id, exitCode, launch.Stopped, stderr))))
                _logger.LogDebug("Dropped the exit of project {ProjectId}: its pipeline is closed", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling the exit of the Claude process for project {ProjectId}", id);
        }
        finally
        {
            // Only now, so a Stop until here finds the launch, marks it stopped and waits for its exit
            _processes.TryRemove(new KeyValuePair<string, Launch>(id, launch));
            launch.Tree.Dispose();
            launch.Process.Dispose();
            launch.Handled.TrySetResult();
        }
    }

    public async Task SendInputAsync(ProjectInfo project, string input)
    {
        if (!_processes.TryGetValue(project.Status.Id, out var launch) || launch.Exited.Task.IsCompleted)
        {
            throw new InvalidOperationException($"No running process found for project {project.Status.Id}");
        }
        if (launch.Stopped)
        {
            throw new InvalidOperationException($"The process of project {project.Status.Id} is being stopped");
        }

        _logger.LogInformation("Sending input to project {ProjectId}: {Input}",
            project.Status.Id,
            input.Length > 50 ? input[..50] + "..." : input);

        // Send json input: {"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."}]}}
        var inputMessage = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = input }
                }
            }
        };
        var json = JsonSerializer.Serialize(inputMessage);
        _logger.LogDebug("Sending JSON to stdin: {Json}", json);

        // One send at a time: StreamWriter rejects a second async write while one is in flight,
        // and input.jsonl is appended by whole-file open
        var stdinLock = project.Process.StdinLock;
        await stdinLock.WaitAsync();
        try
        {
            await launch.Process.StandardInput.WriteLineAsync(json);
            await launch.Process.StandardInput.FlushAsync();

            var inputPath = Path.Combine(project.ProjectPath, ".godmode", "input.jsonl");
            await LogInputAsync(inputPath, input, CancellationToken.None);
        }
        finally
        {
            stdinLock.Release();
        }
    }

    /// <summary>
    /// Stops the project's claude, gracefully first: interrupts it and closes its input, gives it
    /// <paramref name="grace"/> (<see cref="StopGracePeriod"/> by default) to exit, then kills its
    /// process tree, children included. Returns once its exit is on the pipeline, after all its output.
    /// </summary>
    public async Task StopProcessAsync(ProjectInfo project, TimeSpan? grace = null)
    {
        var id = project.Status.Id;
        _logger.LogInformation("Stopping process for project {ProjectId}", id);

        // Cancelling marks the launch stopped through its registration, so no fresh session takes its place
        if (project.Process.Cancellation is { } cancellation)
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            project.Process.Cancellation = null;
        }

        // A fresh session that took its place before the cancel is stopped in turn
        while (_processes.TryGetValue(id, out var launch))
        {
            await StopLaunchAsync(project, launch, grace ?? StopGracePeriod);
            await launch.Handled.Task;
        }

        project.Process.ProcessId = 0;
    }

    private async Task StopLaunchAsync(ProjectInfo project, Launch launch, TimeSpan grace)
    {
        launch.MarkStopped();
        if (!launch.Exited.Task.IsCompleted)
        {
            var deadline = Task.Delay(grace);
            _ = InterruptAsync(project, launch);
            if (await Task.WhenAny(launch.Exited.Task, deadline) == launch.Exited.Task)
                _logger.LogInformation("Claude process {ProcessId} for project {ProjectId} exited when interrupted", launch.Id, project.Status.Id);
            else
                _logger.LogWarning("Claude process {ProcessId} for project {ProjectId} did not exit within {Grace}s of its interrupt; killing it",
                    launch.Id, project.Status.Id, grace.TotalSeconds);
        }
        // All of the session that is left: the process if it outlived the grace period, and any child of it
        launch.Tree.Kill();
    }

    /// <summary>What claude honours whatever it is doing (<see cref="SessionProcessTree.InterruptAsync"/>), then the end of its input, which an idle claude exits on too.</summary>
    private async Task InterruptAsync(ProjectInfo project, Launch launch)
    {
        try { await launch.Tree.InterruptAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not interrupt claude for project {ProjectId}", project.Status.Id); }

        var stdinLock = project.Process.StdinLock;
        if (!await stdinLock.WaitAsync(StdinCloseWait)) return;
        try { launch.Process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { /* exited meanwhile */ }
        finally { stdinLock.Release(); }
    }

    public async Task SettleAsync(ProjectInfo project)
    {
        while (_processes.TryGetValue(project.Status.Id, out var launch) && launch.Exited.Task.IsCompleted)
            await launch.Handled.Task;
    }

    public bool IsProcessRunning(int processId)
    {
        if (processId == 0) return false;

        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private async Task LogInputAsync(string inputPath, string input, CancellationToken cancellationToken)
    {
        var inputEvent = new
        {
            timestamp = DateTime.UtcNow,
            type = "user_input",
            content = input,
            metadata = new { }
        };

        var json = JsonSerializer.Serialize(inputEvent);
        await File.AppendAllTextAsync(inputPath, json + Environment.NewLine, cancellationToken);
    }
}
