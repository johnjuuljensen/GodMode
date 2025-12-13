using GodMode.Server.Models;
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
        var statusPath = Path.Combine(project.ProjectPath, "status.json");

        var status = new ProjectStatus(
            project.Id,
            project.Name,
            project.State,
            project.CreatedAt,
            project.UpdatedAt,
            project.RepoUrl,
            project.CurrentQuestion,
            project.Metrics,
            project.Git,
            project.Tests,
            project.OutputOffset
        );

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(statusPath, json);
    }

    public async Task UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent)
    {
        var stateChanged = false;

        // Parse Claude output events to update state
        switch (outputEvent.Type)
        {
            case OutputEventType.AssistantOutput:
                // Check if it's asking a question (simple heuristic)
                if (outputEvent.Content.Contains("?") && outputEvent.Content.Length < 500)
                {
                    project.State = ProjectState.WaitingInput;
                    project.CurrentQuestion = outputEvent.Content;
                    stateChanged = true;
                }
                break;

            case OutputEventType.Error:
                project.State = ProjectState.Error;
                stateChanged = true;
                break;

            case OutputEventType.System:
                // Check for completion or input request signals
                if (outputEvent.Metadata != null)
                {
                    if (outputEvent.Metadata.TryGetValue("event", out var eventType))
                    {
                        var eventStr = eventType?.ToString() ?? "";

                        if (eventStr == "input_request")
                        {
                            project.State = ProjectState.WaitingInput;
                            if (outputEvent.Metadata.TryGetValue("question", out var question))
                            {
                                project.CurrentQuestion = question?.ToString();
                            }
                            stateChanged = true;
                        }
                        else if (eventStr == "complete")
                        {
                            project.State = ProjectState.Idle;
                            project.CurrentQuestion = null;
                            stateChanged = true;
                        }
                    }

                    // Update metrics from metadata
                    if (outputEvent.Metadata.TryGetValue("input_tokens", out var inputTokens))
                    {
                        if (long.TryParse(inputTokens?.ToString(), out var tokens))
                        {
                            project.Metrics = project.Metrics with { InputTokens = tokens };
                        }
                    }

                    if (outputEvent.Metadata.TryGetValue("output_tokens", out var outputTokens))
                    {
                        if (long.TryParse(outputTokens?.ToString(), out var tokens))
                        {
                            project.Metrics = project.Metrics with { OutputTokens = tokens };
                        }
                    }
                }
                break;

            case OutputEventType.ToolUse:
                project.Metrics = project.Metrics with
                {
                    ToolCalls = project.Metrics.ToolCalls + 1
                };
                break;
        }

        // Update duration
        var duration = DateTime.UtcNow - project.CreatedAt;
        project.Metrics = project.Metrics with { Duration = duration };

        // Update cost estimate (rough calculation: $3/M input tokens, $15/M output tokens for Claude Opus)
        var inputCost = (project.Metrics.InputTokens / 1_000_000m) * 3m;
        var outputCost = (project.Metrics.OutputTokens / 1_000_000m) * 15m;
        project.Metrics = project.Metrics with { CostEstimate = inputCost + outputCost };

        if (stateChanged)
        {
            project.UpdatedAt = DateTime.UtcNow;
        }

        await SaveStatusAsync(project);
    }

    public async Task UpdateGitStatusAsync(ProjectInfo project)
    {
        if (!Directory.Exists(Path.Combine(project.WorkPath, ".git")))
        {
            return;
        }

        try
        {
            // Get current branch
            var branch = await RunGitCommandAsync(project.WorkPath, "rev-parse --abbrev-ref HEAD");

            // Get last commit
            var lastCommit = await RunGitCommandAsync(project.WorkPath, "log -1 --format=%H");

            // Get status
            var statusOutput = await RunGitCommandAsync(project.WorkPath, "status --porcelain");
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

            project.Git = new GitStatus(
                branch.Trim(),
                lastCommit.Trim(),
                uncommittedChanges,
                untrackedFiles
            );

            await SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update git status for project {ProjectId}", project.Id);
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
        if (_gitPollingTimers.ContainsKey(project.Id))
        {
            return;
        }

        var timer = new System.Timers.Timer(interval.TotalMilliseconds);
        timer.Elapsed += async (sender, e) =>
        {
            await UpdateGitStatusAsync(project);
        };
        timer.Start();

        _gitPollingTimers[project.Id] = timer;
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
