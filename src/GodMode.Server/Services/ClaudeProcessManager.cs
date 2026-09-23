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

    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly string _executable;
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public event ProcessExitedHandler? OnProcessExited;

    public ClaudeProcessManager(ILogger<ClaudeProcessManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        _executable = configuration[ExecutableSetting] is { Length: > 0 } executable ? executable : "claude";
    }

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

        var godModePath = Path.Combine(project.ProjectPath, ".godmode");

        // Save session ID to file
        await File.WriteAllTextAsync(
            Path.Combine(godModePath, "session-id"),
            sessionId,
            cancellationToken
        );

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

        // Try --resume first, with session validation callback
        var args = BuildArgs(["--resume", project.SessionId], extraArgs);
        var sessionNotFound = false;

        var processId = await RunClaudeProcessAsync(project, args, null, cancellationToken, extraEnvironment, stderrLine =>
        {
            // Check for session not found error
            if (stderrLine.Contains("No conversation found with session ID:"))
            {
                sessionNotFound = true;
            }
        },
        // The fresh start below launches with the same MCP config
        retainMcpConfig: () => sessionNotFound);

        // If session wasn't found, the process will have exited - start fresh
        if (sessionNotFound)
        {
            _logger.LogWarning("Resume failed for project {ProjectId}, session {SessionId} not found. Starting fresh session.",
                project.Status.Id, project.SessionId);

            var godModePath = Path.Combine(project.ProjectPath, ".godmode");
            await File.WriteAllTextAsync(
                Path.Combine(godModePath, "session-id"),
                project.SessionId,
                cancellationToken
            );

            // Start with new session - send prompt via stdin
            var freshArgs = BuildArgs(["--session-id", project.SessionId], extraArgs);
            return await RunClaudeProcessAsync(project, freshArgs, "Continue from where we left off. Review the codebase and previous work.", cancellationToken, extraEnvironment);
        }

        return processId;
    }

    private static string[] BuildArgs(string[] additionalArgs, string[]? extraArgs = null)
    {
        var result = new List<string>(DefaultArgs);
        result.AddRange(additionalArgs);
        if (extraArgs != null)
            result.AddRange(extraArgs);
        return result.ToArray();
    }

    private Task<int> RunClaudeProcessAsync(ProjectInfo project, string[] args, string? initialPrompt, CancellationToken cancellationToken, Dictionary<string, string>? extraEnvironment = null)
        => RunClaudeProcessAsync(project, args, initialPrompt, cancellationToken, extraEnvironment, onStderrLine: null, retainMcpConfig: null);

    private async Task<int> RunClaudeProcessAsync(
        ProjectInfo project,
        string[] args,
        string? initialPrompt,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment,
        Action<string>? onStderrLine,
        Func<bool>? retainMcpConfig)
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
        foreach (var (key, value) in ChildEnvironment.Build(ChildEnvironment.Current(), extraEnvironment))
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

        var exitedTcs = new TaskCompletionSource<int>();

        // Handle process exit event
        process.Exited += async (sender, e) =>
        {
            var exitCode = process.ExitCode;
            _logger.LogInformation(
                "Claude process exited for project {ProjectId} with exit code {ExitCode} (PID {ProcessId})",
                project.Status.Id, exitCode, process.Id);

            _processes.TryRemove(project.Status.Id, out _);

            exitedTcs.TrySetResult(exitCode);

            // Notify listeners so ProjectManager can update status
            if (OnProcessExited != null)
            {
                try { await OnProcessExited(project, exitCode); }
                catch (Exception ex) { _logger.LogError(ex, "Error in OnProcessExited handler for project {ProjectId}", project.Status.Id); }
            }
        };

        // stdout: only hand the line on. The project's one consumer writes it to output.jsonl, updates
        // state and broadcasts it, in order; doing that here raced the lines of one burst.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && !output.TryWrite(e.Data))
                _logger.LogDebug("Dropped output for project {ProjectId}: its pipeline is closed", project.Status.Id);
        };

        // The MCP config goes once the process has exited and its stderr is drained, so the
        // resume check below has seen every line before retainMcpConfig is asked
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

                onStderrLine?.Invoke(e.Data);

                // Surface error lines to the UI as synthetic error output events, through the same
                // pipeline so they persist to output.jsonl for backfill on refresh
                if (e.Data.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    output.TryWrite(JsonSerializer.Serialize(new { type = "error", error = e.Data }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing stderr for project {ProjectId}", project.Status.Id);
            }
        };

        var launchedAt = DateTime.UtcNow;
        _ = DeleteMcpConfigAfterExitAsync(project, launchedAt, Task.WhenAll(exitedTcs.Task, stderrClosed.Task), retainMcpConfig);

        _processes[project.Status.Id] = process;

        _logger.LogInformation("Starting Claude process for project {ProjectId} with args: {Args}",
            project.Status.Id, string.Join(" ", args));

        try
        {
            process.Start();
        }
        catch
        {
            // Never started: no Exited event will come, so release the MCP config cleanup here
            _processes.TryRemove(project.Status.Id, out _);
            stderrWriter.Dispose();
            exitedTcs.TrySetResult(-1);
            stderrClosed.TrySetResult();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Claude process started for project {ProjectId} with PID {ProcessId}",
            project.Status.Id, process.Id);

        // Handle cancellation
        cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation("Cancellation requested, killing process for project {ProjectId}", project.Status.Id);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error killing process for project {ProjectId}", project.Status.Id);
            }
        });

        // If we have a stderr callback, wait for the process to exit quickly (for validation)
        // This allows us to detect immediate failures like "session not found"
        if (onStderrLine != null)
        {
            // Wait for either process exit or a short timeout
            var completed = await Task.WhenAny(exitedTcs.Task, Task.Delay(1000, cancellationToken));

            if (completed == exitedTcs.Task)
            {
                _logger.LogInformation("Process exited quickly for project {ProjectId}", project.Status.Id);
            }
        }


        // Send initial prompt via stdin if provided
        if ( !string.IsNullOrEmpty( initialPrompt ) ) {
            await SendInputAsync( project, initialPrompt );
        }


        return process.Id;
    }

    private async Task DeleteMcpConfigAfterExitAsync(ProjectInfo project, DateTime launchedAt, Task exited, Func<bool>? retain)
    {
        await exited;
        if (retain?.Invoke() == true) return;
        try { McpConfigFile.DeleteIfWrittenBefore(project.ProjectPath, launchedAt); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the MCP config for project {ProjectId}", project.Status.Id); }
    }

    public async Task SendInputAsync(ProjectInfo project, string input)
    {
        if (!_processes.TryGetValue(project.Status.Id, out var process) || process.HasExited)
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
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();

            var inputPath = Path.Combine(project.ProjectPath, ".godmode", "input.jsonl");
            await LogInputAsync(inputPath, input, CancellationToken.None);
        }
        finally
        {
            stdinLock.Release();
        }
    }

    public async Task StopProcessAsync(ProjectInfo project)
    {
        _logger.LogInformation("Stopping process for project {ProjectId}", project.Status.Id);

        if (project.Process.Cancellation is { } cancellation)
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
            project.Process.Cancellation = null;
        }

        if (_processes.TryRemove(project.Status.Id, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping process for project {ProjectId}", project.Status.Id);
            }
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
