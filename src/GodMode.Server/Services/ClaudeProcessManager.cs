using CliWrap;
using CliWrap.Buffered;
using GodMode.Server.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Manages Claude Code processes using CliWrap.
/// </summary>
public class ClaudeProcessManager : IClaudeProcessManager
{
    private readonly ILogger<ClaudeProcessManager> _logger;
    private readonly ConcurrentDictionary<string, StreamWriter> _inputStreams = new();

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

        var inputPath = Path.Combine(godModePath, "input.jsonl");
        var outputPath = Path.Combine(godModePath, "output.jsonl");

        // Create input pipe
        var inputPipe = PipeSource.Create(async (stream, ct) =>
        {
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            _inputStreams[project.Id] = writer;

            // Send initial prompt
            await writer.WriteLineAsync(initialPrompt.AsMemory(), ct);
            await writer.FlushAsync(ct);

            // Log input
            await LogInputAsync(inputPath, initialPrompt, ct);

            // Keep stream open for future input
            await Task.Delay(Timeout.Infinite, ct);
        });

        // Create output pipe
        var outputPipe = PipeTarget.Create(async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var fileStream = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var fileWriter = new StreamWriter(fileStream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                // Write to output.jsonl
                await fileWriter.WriteLineAsync(line.AsMemory(), ct);
                await fileWriter.FlushAsync(ct);

                _logger.LogDebug("Claude output: {Output}", line.Length > 100 ? line.Substring(0, 100) + "..." : line);
            }
        });

        // Start the process
        var command = Cli.Wrap("claude")
            .WithArguments(["--print", "--verbose", " --dangerously-skip-permissions", "--output-format=stream-json", "--session-id", sessionId, initialPrompt])
            .WithWorkingDirectory(project.WorkPath)
            .WithStandardInputPipe(inputPipe)
            .WithStandardOutputPipe(outputPipe)
            .WithValidation(CommandResultValidation.None);

        var commandTask = command.ExecuteAsync(cancellationToken);
        
        // Note: We can't easily get the process ID from CliWrap, so we'll use 0 as a placeholder
        // The cancellation token is used to stop the process
        return 0;
    }

    public async Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resuming Claude process for project {ProjectId} with session {SessionId}",
            project.Id, project.SessionId);

        if (string.IsNullOrEmpty(project.SessionId))
        {
            throw new InvalidOperationException($"Cannot resume project {project.Id}: no session ID found");
        }

        var godModePath = Path.Combine(project.ProjectPath, ".godmode");
        var inputPath = Path.Combine(godModePath, "input.jsonl");
        var outputPath = Path.Combine(godModePath, "output.jsonl");

        // Create input pipe
        var inputPipe = PipeSource.Create(async (stream, ct) =>
        {
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            _inputStreams[project.Id] = writer;

            // Keep stream open for future input
            await Task.Delay(Timeout.Infinite, ct);
        });

        // Create output pipe
        var outputPipe = PipeTarget.Create(async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var fileStream = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var fileWriter = new StreamWriter(fileStream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                // Write to output.jsonl
                await fileWriter.WriteLineAsync(line.AsMemory(), ct);
                await fileWriter.FlushAsync(ct);

                _logger.LogDebug("Claude output: {Output}", line.Length > 100 ? line.Substring(0, 100) + "..." : line);
            }
        });

        // Start the process with --resume flag (uses existing session)
        var command = Cli.Wrap("claude")
            .WithArguments(["--print", "--verbose", "--dangerously-skip-permissions", "--output-format=stream-json", "--resume", project.SessionId])
            .WithWorkingDirectory(project.WorkPath)
            .WithStandardInputPipe(inputPipe)
            .WithStandardOutputPipe(outputPipe)
            .WithValidation(CommandResultValidation.None);

        var commandTask = command.ExecuteAsync(cancellationToken);

        return 0;
    }

    public async Task SendInputAsync(ProjectInfo project, string input)
    {
        if (!_inputStreams.TryGetValue(project.Id, out var writer))
        {
            throw new InvalidOperationException($"No input stream found for project {project.Id}");
        }

        _logger.LogInformation("Sending input to project {ProjectId}: {Input}", 
            project.Id, 
            input.Length > 50 ? input.Substring(0, 50) + "..." : input);

        await writer.WriteLineAsync(input);
        await writer.FlushAsync();

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

        if (_inputStreams.TryRemove(project.Id, out var writer))
        {
            try
            {
                await writer.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing input stream for project {ProjectId}", project.Id);
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
