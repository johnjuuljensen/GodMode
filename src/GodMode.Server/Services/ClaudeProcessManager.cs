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
        "--debug",
        "--replay-user-messages",
        "--verbose",
        "--output-format=stream-json",
        "--input-format=stream-json"
    ];

    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public event OutputReceivedHandler? OnOutputReceived;
    public event ProcessExitedHandler? OnProcessExited;

    public ClaudeProcessManager(ILogger<ClaudeProcessManager> logger)
    {
        _logger = logger;
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
        });

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
        => RunClaudeProcessAsync(project, args, initialPrompt, cancellationToken, extraEnvironment, onStderrLine: null);

    private async Task<int> RunClaudeProcessAsync(
        ProjectInfo project,
        string[] args,
        string? initialPrompt,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment = null,
        Action<string>? onStderrLine = null)
    {
        var godModePath = Path.Combine(project.ProjectPath, ".godmode");
        var outputPath = Path.Combine(godModePath, "output.jsonl");
        var stderrPath = Path.Combine(godModePath, "errs.txt");

        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
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

        // Set extra environment variables from root config
        if (extraEnvironment != null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Open output files for writing
        var outputStream = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var outputWriter = new StreamWriter(outputStream, Encoding.UTF8) { AutoFlush = true };
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

            // Clean up streams
            outputWriter.Dispose();
            outputStream.Dispose();
            stderrWriter.Dispose();
            stderrStream.Dispose();

            exitedTcs.TrySetResult(exitCode);

            // Notify listeners so ProjectManager can update status
            if (OnProcessExited != null)
            {
                try { await OnProcessExited(project, exitCode); }
                catch (Exception ex) { _logger.LogError(ex, "Error in OnProcessExited handler for project {ProjectId}", project.Status.Id); }
            }
        };

        // Handle stdout data received
        process.OutputDataReceived += async (sender, e) =>
        {
            if (e.Data == null) return;

            try
            {
                outputWriter.WriteLine(e.Data);

                _logger.LogInformation("Claude output [{ProjectId}]: {Output}",
                    project.Status.Id,
                    e.Data.Length > 200 ? e.Data[..200] + "..." : e.Data);

                // Raise event to notify listeners
                if (OnOutputReceived != null)
                {
                    try
                    {
                        await OnOutputReceived(project, e.Data);
                    }
                    catch (Exception eventEx)
                    {
                        _logger.LogError(eventEx, "Error in OnOutputReceived handler for project {ProjectId}", project.Status.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing stdout for project {ProjectId}", project.Status.Id);
            }
        };

        // Handle stderr data received
        process.ErrorDataReceived += async (sender, e) =>
        {
            if (e.Data == null) return;

            try
            {
                stderrWriter.WriteLine($"[{DateTime.UtcNow:O}] {e.Data}");
                _logger.LogWarning("Claude stderr [{ProjectId}]: {Error}", project.Status.Id, e.Data);

                onStderrLine?.Invoke(e.Data);

                // Surface error lines to the UI as synthetic error output events
                if (e.Data.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    var errorJson = JsonSerializer.Serialize(new { type = "error", error = e.Data });

                    // Write to output.jsonl so it persists for backfill on refresh
                    try { outputWriter.WriteLine(errorJson); }
                    catch { /* stream may be disposed if process exited */ }

                    if (OnOutputReceived != null)
                    {
                        try { await OnOutputReceived(project, errorJson); }
                        catch (Exception eventEx) { _logger.LogError(eventEx, "Error in OnOutputReceived for stderr"); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing stderr for project {ProjectId}", project.Status.Id);
            }
        };

        _processes[project.Status.Id] = process;

        _logger.LogInformation("Starting Claude process for project {ProjectId} with args: {Args}",
            project.Status.Id, string.Join(" ", args));

        process.Start();
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

        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();

        // Log input
        var inputPath = Path.Combine(project.ProjectPath, ".godmode", "input.jsonl");
        await LogInputAsync(inputPath, input, CancellationToken.None);
    }

    public async Task StopProcessAsync(ProjectInfo project)
    {
        _logger.LogInformation("Stopping process for project {ProjectId}", project.Status.Id);

        if (project.ProcessCancellation != null)
        {
            await project.ProcessCancellation.CancelAsync();
            project.ProcessCancellation.Dispose();
            project.ProcessCancellation = null;
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

        project.ProcessId = 0;
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
