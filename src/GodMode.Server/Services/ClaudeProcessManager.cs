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

    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly string _executable;
    private readonly ConcurrentDictionary<string, Launch> _processes = new();

    public ClaudeProcessManager(ILogger<ClaudeProcessManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        _executable = configuration[ExecutableSetting] is { Length: > 0 } executable ? executable : "claude";
    }

    /// <summary>One claude process, from its start until its exit has been handed to the pipeline.</summary>
    private sealed class Launch(Process process)
    {
        public Process Process { get; } = process;
        public int Id { get; set; }

        /// <summary>Completes once the exit is on the pipeline (or a fallback launch replaced it).</summary>
        public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _killed;
        public bool Killed => _killed;

        /// <summary>Marks the launch killed, so its exit is not reported as a failure, and kills its tree if it still runs.</summary>
        public void Kill()
        {
            _killed = true;
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* exited and disposed meanwhile */ }
        }
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
        _logger.LogInformation("Resuming Claude process for project {ProjectId} with session {SessionId}",
            project.Status.Id, project.SessionId);

        if (string.IsNullOrEmpty(project.SessionId))
        {
            throw new InvalidOperationException($"Cannot resume project {project.Status.Id}: no session ID found");
        }

        var args = BuildArgs(["--resume", project.SessionId], extraArgs);

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
                    await SessionIdFile.WriteAsync(project.ProjectPath, project.SessionId, cancellationToken);
                    var freshArgs = BuildArgs(["--session-id", project.SessionId], extraArgs);
                    await RunClaudeProcessAsync(project, freshArgs, "Continue from where we left off. Review the codebase and previous work.",
                        cancellationToken, extraEnvironment);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not start a fresh session for project {ProjectId}", project.Status.Id);
                    return false;
                }
            });
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
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
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

        // stdout goes to the project's output pipeline, whose consumer writes output.jsonl
        var stderrStream = new FileStream(stderrPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var stderrWriter = new StreamWriter(stderrStream, Encoding.UTF8) { AutoFlush = true };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var launch = new Launch(process);

        // The exit is handled once the process has exited and both pipes are drained, so it
        // follows every line the process wrote
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTail = new ConcurrentQueue<string>();

        process.Exited += (_, _) => exited.TrySetResult();

        // stdout: only hand the line on. The project's one consumer writes it to output.jsonl, updates
        // state and broadcasts it, in order; doing that here raced the lines of one burst.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                stdoutClosed.TrySetResult();
            else if (!output.TryWrite(new PipelineItem.Line(e.Data)))
                _logger.LogDebug("Dropped output for project {ProjectId}: its pipeline is closed", project.Status.Id);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stderrWriter.Dispose();
                stderrClosed.TrySetResult();
                return;
            }

            try
            {
                stderrWriter.WriteLine($"[{DateTime.UtcNow:O}] {e.Data}");
                _logger.LogWarning("Claude stderr [{ProjectId}]: {Error}", project.Status.Id, e.Data);

                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > StderrTailLines) stderrTail.TryDequeue(out string? _);

                // Show error lines in the UI as synthetic error output events, through the same
                // pipeline so they persist to output.jsonl for backfill on refresh. They do not
                // change the project's state: the exit and error results do.
                if (e.Data.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    output.TryWrite(new PipelineItem.Line(JsonSerializer.Serialize(new { type = "error", error = e.Data })));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing stderr for project {ProjectId}", project.Status.Id);
            }
        };

        _logger.LogInformation("Starting Claude process for project {ProjectId} with args: {Args}",
            project.Status.Id, string.Join(" ", args));

        var launchedAt = DateTime.UtcNow;
        try
        {
            process.Start();
        }
        catch
        {
            // Never started: there is no exit to handle
            stderrWriter.Dispose();
            process.Dispose();
            throw;
        }

        launch.Id = process.Id;
        _processes[project.Status.Id] = launch;
        project.Process.ProcessId = launch.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Claude process started for project {ProjectId} with PID {ProcessId}",
            project.Status.Id, launch.Id);

        var cancellation = cancellationToken.Register(() =>
        {
            try
            {
                _logger.LogInformation("Cancellation requested, killing process for project {ProjectId}", project.Status.Id);
                launch.Kill();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error killing process for project {ProjectId}", project.Status.Id);
            }
        });

        _ = HandleExitAsync(project, launch, launchedAt, exited.Task, Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
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
    /// else deletes its MCP config, clears its PID and puts its exit on the pipeline, last.
    /// </summary>
    private async Task HandleExitAsync(ProjectInfo project, Launch launch, DateTime launchedAt, Task exited, Task drained,
        ConcurrentQueue<string> stderrTail, ExitTakeover? takeover, CancellationTokenRegistration cancellation)
    {
        var id = project.Status.Id;
        try
        {
            await exited;
            if (await Task.WhenAny(drained, Task.Delay(PipeDrainTimeout)) != drained)
                _logger.LogWarning("Claude process {ProcessId} for project {ProjectId} exited, but its output is still open; handling the exit without it",
                    launch.Id, id);
            await cancellation.DisposeAsync();

            var exitCode = launch.Process.ExitCode;
            var tail = stderrTail.ToArray();
            _logger.LogInformation("Claude process exited for project {ProjectId} with exit code {ExitCode} (PID {ProcessId}{Killed})",
                id, exitCode, launch.Id, launch.Killed ? ", killed" : "");

            if (!launch.Killed && takeover != null && await takeover(exitCode, tail))
                return;

            try { McpConfigFile.DeleteIfWrittenBefore(project.ProjectPath, launchedAt); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the MCP config for project {ProjectId}", id); }

            project.Process.ClearProcessId(launch.Id);
            var stderr = tail.Length > 0 ? string.Join("\n", tail) : null;
            if (!project.Process.Output.TryWrite(new PipelineItem.Exited(new ProcessExit(launch.Id, exitCode, launch.Killed, stderr))))
                _logger.LogDebug("Dropped the exit of project {ProjectId}: its pipeline is closed", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling the exit of the Claude process for project {ProjectId}", id);
        }
        finally
        {
            // Only now, so a Stop until here finds the launch, marks it killed and waits for its exit
            _processes.TryRemove(new KeyValuePair<string, Launch>(id, launch));
            launch.Process.Dispose();
            launch.Handled.TrySetResult();
        }
    }

    public async Task SendInputAsync(ProjectInfo project, string input)
    {
        if (!_processes.TryGetValue(project.Status.Id, out var launch) || launch.Process.HasExited)
        {
            throw new InvalidOperationException($"No running process found for project {project.Status.Id}");
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

    /// <summary>Kills the process tree and returns once its exit is on the pipeline, after all its output.</summary>
    public async Task StopProcessAsync(ProjectInfo project)
    {
        _logger.LogInformation("Stopping process for project {ProjectId}", project.Status.Id);

        // Cancelling kills the launch through its registration
        if (project.Process.Cancellation is { } cancellation)
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            project.Process.Cancellation = null;
        }

        if (_processes.TryGetValue(project.Status.Id, out var launch))
        {
            try { launch.Kill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping process for project {ProjectId}", project.Status.Id); }
            await launch.Handled.Task;
        }

        project.Process.ProcessId = 0;
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
