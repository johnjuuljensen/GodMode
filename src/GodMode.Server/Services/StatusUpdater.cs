using GodMode.Server.Models;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Diagnostics;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Updates status.json based on Claude output events and git status.
/// </summary>
public class StatusUpdater : IStatusUpdater
{
    private readonly ILogger<StatusUpdater> _logger;
    private readonly Dictionary<string, System.Timers.Timer> _gitPollingTimers = new();

    public StatusUpdater(ILogger<StatusUpdater> logger)
    {
        _logger = logger;
    }

    public async Task SaveStatusAsync(ProjectInfo project)
    {
        var statusPath = Path.Combine(project.ProjectPath, ".godmode", "status.json");

        var json = JsonSerializer.Serialize(project.Status, JsonDefaults.Options);

        await File.WriteAllTextAsync(statusPath, json);
    }

    public async Task UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent)
    {
        var stateChanged = false;
        var status = project.Status;

        // Parse Claude output events to update state
        switch (outputEvent.Type)
        {
            case OutputEventType.Assistant:
                // Check if it's asking a question (simple heuristic)
                if (!string.IsNullOrEmpty(outputEvent.Content) &&
                    outputEvent.Content.Contains("?") && outputEvent.Content.Length < 500)
                {
                    status = status with { State = ProjectState.WaitingInput, CurrentQuestion = outputEvent.Content };
                    stateChanged = true;
                }
                break;

            case OutputEventType.Error:
                status = status with { State = ProjectState.Error };
                stateChanged = true;
                break;

            case OutputEventType.Result:
                // Result indicates the turn is complete
                status = status with { State = ProjectState.Idle, CurrentQuestion = null };
                stateChanged = true;

                // Update metrics from result metadata
                if (outputEvent.Metadata != null)
                {
                    if (outputEvent.Metadata.TryGetValue("input_tokens", out var inputTokens))
                    {
                        if (long.TryParse(inputTokens?.ToString(), out var tokens))
                        {
                            status = status with { Metrics = status.Metrics with { InputTokens = tokens } };
                        }
                    }

                    if (outputEvent.Metadata.TryGetValue("output_tokens", out var outputTokens))
                    {
                        if (long.TryParse(outputTokens?.ToString(), out var tokens))
                        {
                            status = status with { Metrics = status.Metrics with { OutputTokens = tokens } };
                        }
                    }
                }
                break;

            case OutputEventType.System:
                // System init event - project is running
                status = status with { State = ProjectState.Running };
                stateChanged = true;
                break;
        }

        // Update duration
        var duration = DateTime.UtcNow - status.CreatedAt;
        status = status with { Metrics = status.Metrics with { Duration = duration } };

        // Update cost estimate (rough calculation: $3/M input tokens, $15/M output tokens for Claude Opus)
        var inputCost = (status.Metrics.InputTokens / 1_000_000m) * 3m;
        var outputCost = (status.Metrics.OutputTokens / 1_000_000m) * 15m;
        status = status with { Metrics = status.Metrics with { CostEstimate = inputCost + outputCost } };

        if (stateChanged)
        {
            status = status with { UpdatedAt = DateTime.UtcNow };
        }

        project.Status = status;
        await SaveStatusAsync(project);
    }

    public async Task UpdateGitStatusAsync(ProjectInfo project)
    {
        if (!Directory.Exists(Path.Combine(project.ProjectPath, ".git")))
        {
            return;
        }

        try
        {
            // Get current branch
            var branch = await RunGitCommandAsync(project.ProjectPath, "rev-parse --abbrev-ref HEAD");

            // Get last commit
            var lastCommit = await RunGitCommandAsync(project.ProjectPath, "log -1 --format=%H");

            // Get status
            var statusOutput = await RunGitCommandAsync(project.ProjectPath, "status --porcelain");
            var lines = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var uncommittedChanges = 0;
            var untrackedFiles = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("??"))
                {
                    untrackedFiles++;
                }
                else
                {
                    uncommittedChanges++;
                }
            }

            project.Status = project.Status with
            {
                Git = new GitStatus(
                    branch.Trim(),
                    lastCommit.Trim(),
                    uncommittedChanges,
                    untrackedFiles
                )
            };

            await SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update git status for project {ProjectId}", project.Status.Id);
        }
    }

    private async Task<string> RunGitCommandAsync(string workPath, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output;
    }

    public void StartGitPolling(ProjectInfo project, TimeSpan interval)
    {
        if (_gitPollingTimers.ContainsKey(project.Status.Id))
        {
            return;
        }

        var timer = new System.Timers.Timer(interval.TotalMilliseconds);
        timer.Elapsed += async (sender, e) =>
        {
            await UpdateGitStatusAsync(project);
        };
        timer.Start();

        _gitPollingTimers[project.Status.Id] = timer;
    }

    public void StopGitPolling(string projectId)
    {
        if (_gitPollingTimers.TryGetValue(projectId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            _gitPollingTimers.Remove(projectId);
        }
    }
}
