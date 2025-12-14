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
        "--verbose",
        "--dangerously-skip-permissions",
        "--output-format=stream-json"
    ];

    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public ClaudeProcessManager(ILogger<ClaudeProcessManager> logger)
    {
        _logger = logger;
    }

    public async Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Claude process for project {ProjectId}", project.Id);

        var sessionId = project.SessionId ?? Guid.NewGuid().ToString();
        project.SessionId = sessionId;

        var godModePath = Path.Combine(project.ProjectPath, ".godmode");

        // Save session ID to file
        await File.WriteAllTextAsync(
            Path.Combine(godModePath, "session-id"),
            sessionId,
            cancellationToken
        );

        var args = BuildArgs(["--session-id", sessionId, initialPrompt]);
        return await RunClaudeProcessAsync(project, args, cancellationToken);
    }

    public Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resuming Claude process for project {ProjectId} with session {SessionId}",
            project.Id, project.SessionId);

        if (string.IsNullOrEmpty(project.SessionId))
        {
            throw new InvalidOperationException($"Cannot resume project {project.Id}: no session ID found");
        }

        var args = BuildArgs(["--resume", project.SessionId]);
        return RunClaudeProcessAsync(project, args, cancellationToken);
    }

    private static string[] BuildArgs(string[] additionalArgs) => [..DefaultArgs, ..additionalArgs];

    private async Task<int> RunClaudeProcessAsync(ProjectInfo project, string[] args, CancellationToken cancellationToken)
    {
        var godModePath = Path.Combine(project.ProjectPath, ".godmode");
        var outputPath = Path.Combine(godModePath, "output.jsonl");

        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = project.WorkPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _processes[project.Id] = process;

        process.Start();

        // Handle cancellation
        cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error killing process for project {ProjectId}", project.Id);
            }
        });

        // Read stdout in background and write to output.jsonl
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fileStream = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                await using var fileWriter = new StreamWriter(fileStream, Encoding.UTF8);

                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
                {
                    await fileWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
                    await fileWriter.FlushAsync(cancellationToken);

                    _logger.LogDebug("Claude output: {Output}", line.Length > 100 ? line[..100] + "..." : line);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading stdout for project {ProjectId}", project.Id);
            }
        }, cancellationToken);

        // Read stderr in background
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) != null)
                {
                    _logger.LogWarning("Claude stderr [{ProjectId}]: {Error}", project.Id, line);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading stderr for project {ProjectId}", project.Id);
            }
        }, cancellationToken);

        return process.Id;
    }

    public async Task SendInputAsync(ProjectInfo project, string input)
    {
        if (!_processes.TryGetValue(project.Id, out var process) || process.HasExited)
        {
            throw new InvalidOperationException($"No running process found for project {project.Id}");
        }

        _logger.LogInformation("Sending input to project {ProjectId}: {Input}",
            project.Id,
            input.Length > 50 ? input[..50] + "..." : input);

        await process.StandardInput.WriteLineAsync(input);
        await process.StandardInput.FlushAsync();

        // Log input
        var inputPath = Path.Combine(project.ProjectPath, ".godmode", "input.jsonl");
        await LogInputAsync(inputPath, input, CancellationToken.None);
    }

    public async Task StopProcessAsync(ProjectInfo project)
    {
        _logger.LogInformation("Stopping process for project {ProjectId}", project.Id);

        if (project.ProcessCancellation != null)
        {
            await project.ProcessCancellation.CancelAsync();
            project.ProcessCancellation.Dispose();
            project.ProcessCancellation = null;
        }

        if (_processes.TryRemove(project.Id, out var process))
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
                _logger.LogWarning(ex, "Error stopping process for project {ProjectId}", project.Id);
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
