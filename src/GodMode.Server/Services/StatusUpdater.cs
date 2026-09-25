using GodMode.ProjectFiles;
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
        // A project without one has nowhere to keep its status: a create that failed before its
        // script made the folder, or a folder removed outside GodMode. Making it would make the
        // folder, which a create script expects not to find. Its status is in memory until it is deleted
        var godModePath = Path.Combine(project.ProjectPath, ".godmode");
        if (!Directory.Exists(godModePath))
        {
            _logger.LogDebug("Project {ProjectId} has no {Path}; its status is not saved", project.Status.Id, godModePath);
            return;
        }
        var statusPath = Path.Combine(godModePath, "status.json");

        var json = JsonSerializer.Serialize(project.Status, JsonDefaults.Options);

        // Atomic, so a reader (recovery, a restarted server) never meets a half-written file
        await AtomicFile.WriteAllTextAsync(statusPath, json);
    }

    public async Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson)
    {
        var stateChanged = false;
        var status = project.Status;
        var process = project.Process;

        // Parse Claude output events to update state
        switch (outputEvent.Type)
        {
            case OutputEventType.User:
                // A new turn is starting — clear any memo of the previous turn's
                // trailing assistant text so stale questions don't leak forward.
                process.LastAssistantText = null;
                // claude has taken a message the user sent: it is working on it, whatever a result
                // of an earlier turn, handled after the send, said. It echoes it at once between
                // turns, and at its next step in one
                if (IsEcho(outputEvent) && status is { PendingPermission: null, PendingQuestion: null }
                    && (status.State != ProjectState.Running || status.CurrentQuestion != null))
                {
                    status = status with { State = ProjectState.Running, CurrentQuestion = null };
                    stateChanged = true;
                }
                break;

            case OutputEventType.Assistant:
                // Remember the last text content block from this assistant event.
                // The deterministic question check happens on Result (end of turn).
                // Tool-only assistant events return null here; don't overwrite
                // a previously-seen text block in that case.
                var lastText = QuestionDetection.ExtractLastAssistantText(rawJson);
                if (lastText != null) process.LastAssistantText = lastText;
                break;

            // Error events are stderr lines shown in the UI; the process's exit and error results
            // decide whether the session failed

            // claude's answer to the interrupt a stop sends: the stop decides the state
            case OutputEventType.Result when IsErrorResult(outputEvent) && process.Stopping:
                process.LastAssistantText = null;
                break;

            case OutputEventType.Result when IsErrorResult(outputEvent):
                status = status with
                {
                    State = ProjectState.Error,
                    CurrentQuestion = null,
                    LastError = outputEvent.Content is { Length: > 0 } text ? text : Subtype(outputEvent) ?? "error result",
                };
                process.LastAssistantText = null;
                stateChanged = true;
                status = WithTokenMetrics(status, outputEvent);
                break;

            case OutputEventType.Result:
                // End of turn: decide Idle vs WaitingInput based on whether the
                // last assistant text block (trimmed) ends with '?'. See issue #131.
                // The result's text is claude's summary of the turn, whichever it is
                var endedAt = DateTime.UtcNow;
                status = status with { LastResult = outputEvent.Content, LastResultAt = endedAt, LastError = null };
                status = QuestionDetection.IsQuestion(process.LastAssistantText)
                    ? status with { State = ProjectState.WaitingInput, CurrentQuestion = process.LastAssistantText, QuestionAt = endedAt }
                    : status with { State = ProjectState.Idle, CurrentQuestion = null };
                process.LastAssistantText = null;
                stateChanged = true;
                status = WithTokenMetrics(status, outputEvent);
                break;

            case OutputEventType.System when IsSessionStart(outputEvent):
                // The session claude keeps is the one it reports, which a resume must name
                if (outputEvent.Metadata?.GetValueOrDefault(SessionIdKey) is string sessionId && sessionId != project.SessionId)
                {
                    _logger.LogInformation("Project {ProjectId} runs session {SessionId} (asked for {Requested})",
                        project.Status.Id, sessionId, project.SessionId);
                    project.SessionId = sessionId;
                    // A write that fails is not the session failing: it is written again on the next init
                    try { await SessionIdFile.WriteAsync(project.ProjectPath, sessionId); }
                    catch (Exception ex) { _logger.LogError(ex, "Could not save the session id of project {ProjectId}", project.Status.Id); }
                }
                // The session (re)started - project is running
                stateChanged = status.State != ProjectState.Running || status.LastError != null;
                status = status with { State = ProjectState.Running, LastError = null };
                break;
        }

        // Most lines (assistant text, tool use, echoed user messages) change nothing on disk
        if (!stateChanged) return false;

        // Update duration
        var duration = DateTime.UtcNow - status.CreatedAt;
        status = status with { Metrics = status.Metrics with { Duration = duration } };

        // Update cost estimate (rough calculation: $3/M input tokens, $15/M output tokens for Claude Opus)
        var inputCost = (status.Metrics.InputTokens / 1_000_000m) * 3m;
        var outputCost = (status.Metrics.OutputTokens / 1_000_000m) * 15m;
        status = status with { Metrics = status.Metrics with { CostEstimate = inputCost + outputCost } };

        project.Status = status with { UpdatedAt = DateTime.UtcNow };
        return true;
    }

    /// <summary>The metadata key a <c>system</c> event carries claude's session ID under.</summary>
    public const string SessionIdKey = "session_id";

    /// <summary>The metadata key a <c>user</c> event that claude echoed (<c>isReplay</c>) carries.</summary>
    public const string IsReplayKey = "is_replay";

    /// <summary>A user message the user sent, echoed by claude as it takes it (<c>--replay-user-messages</c>).</summary>
    private static bool IsEcho(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault(IsReplayKey) is true;

    /// <summary><c>system/init</c>: claude (re)started its session. It writes it once it has read its first input.</summary>
    public static bool IsSessionStart(OutputEvent outputEvent) =>
        outputEvent.Type == OutputEventType.System && Subtype(outputEvent) == "init";

    private static string? Subtype(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault("subtype") as string;

    /// <summary><c>is_error</c>, or for a CLI that omits it, a subtype other than <c>success</c>.</summary>
    private static bool IsErrorResult(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault("is_error") is bool isError
            ? isError
            : Subtype(outputEvent) is { } subtype && subtype != "success";

    /// <summary>Takes the token counts from a result's metadata.</summary>
    private static ProjectStatus WithTokenMetrics(ProjectStatus status, OutputEvent outputEvent)
    {
        if (outputEvent.Metadata == null) return status;

        if (outputEvent.Metadata.TryGetValue("input_tokens", out var inputTokens) && long.TryParse(inputTokens?.ToString(), out var input))
            status = status with { Metrics = status.Metrics with { InputTokens = input } };

        if (outputEvent.Metadata.TryGetValue("output_tokens", out var outputTokens) && long.TryParse(outputTokens?.ToString(), out var output))
            status = status with { Metrics = status.Metrics with { OutputTokens = output } };

        return status;
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
