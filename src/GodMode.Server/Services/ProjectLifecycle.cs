using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GodMode.Server.Hubs;
using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Services;

/// <summary>
/// Runs each project's Claude process: start, resume, input and stop, and the one consumer that
/// handles its output. The consumer takes the project's lines in the order claude wrote them and,
/// for each, appends it to <c>output.jsonl</c>, updates state, then broadcasts it, so state derived
/// from output is deterministic. The process's exit comes last, after all its lines, and a Stop
/// marks the project Stopped behind whatever is still queued. Every change the consumer makes to
/// the status is pushed to all clients. Per-project state lives in <see cref="ProjectInfo.Process"/>.
/// </summary>
public sealed class ProjectLifecycle
{
    private readonly IClaudeProcessManager _processManager;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ProjectLifecycle> _logger;

    /// <summary>Raised, on the project's consumer, when output takes a project to Idle. A handler must not wait for a Stop.</summary>
    public event Func<string, Task>? OnProjectCompleted;

    public ProjectLifecycle(
        IClaudeProcessManager processManager,
        IStatusUpdater statusUpdater,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        ILogger<ProjectLifecycle> logger)
    {
        _processManager = processManager;
        _statusUpdater = statusUpdater;
        _hubContext = hubContext;
        _logger = logger;
    }

    // ── Process ──

    public Task StartAsync(ProjectInfo project, string initialPrompt,
        Dictionary<string, string>? environment, string[]? args)
    {
        var process = BeginLaunch(project);
        return _processManager.StartClaudeProcessAsync(project, initialPrompt, process.Cancellation!.Token, environment, args);
    }

    public Task ResumeAsync(ProjectInfo project, Dictionary<string, string>? environment, string[]? args)
    {
        var process = BeginLaunch(project);
        return _processManager.ResumeClaudeProcessAsync(project, process.Cancellation!.Token, environment, args);
    }

    /// <summary>Replaces the previous launch's cancellation and makes sure output has its consumer.</summary>
    private ProjectProcess BeginLaunch(ProjectInfo project)
    {
        var process = project.Process;
        if (process.Cancellation is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }
        process.Cancellation = new CancellationTokenSource();
        EnsureConsumer(project);
        return process;
    }

    private void EnsureConsumer(ProjectInfo project) =>
        project.Process.EnsureConsumer(items => ConsumeAsync(project, items));

    public bool IsRunning(ProjectInfo project) => _processManager.IsProcessRunning(project.Process.ProcessId);

    /// <summary>Kills the process tree; its output and exit are still handled, but change nothing after it.</summary>
    public Task KillAsync(ProjectInfo project) => _processManager.StopProcessAsync(project);

    /// <summary>
    /// Kills the process tree, then marks the project Stopped once every line it wrote has been
    /// handled, so a result still queued cannot turn Stopped back into Idle.
    /// </summary>
    public async Task StopAsync(ProjectInfo project)
    {
        await KillAsync(project);
        await InOrderAsync(project, () => SetStatusAsync(project, status => status with
        {
            State = ProjectState.Stopped,
            UpdatedAt = DateTime.UtcNow
        }));
    }

    /// <summary>Kills the process and lets the consumer finish, before the project is removed.</summary>
    public async Task CloseAsync(ProjectInfo project)
    {
        await KillAsync(project);
        await project.Process.CloseAsync();
    }

    /// <summary>
    /// Sends user input and marks the project Running, under the state lock: a reply that lands
    /// before Running is set waits for it, so Running can never overwrite the reply's state.
    /// </summary>
    public Task SendInputAsync(ProjectInfo project, string input) =>
        WithStateLockAsync(project, async () =>
        {
            await _processManager.SendInputAsync(project, input);
            await SetStatusAsync(project, status => status with
            {
                State = ProjectState.Running,
                CurrentQuestion = null,
                UpdatedAt = DateTime.UtcNow
            });
        });

    // ── State ──

    /// <summary>Changes the project's status and saves it, under the lock the consumer also takes.</summary>
    public Task UpdateStatusAsync(ProjectInfo project, Func<ProjectStatus, ProjectStatus> change) =>
        WithStateLockAsync(project, () => SetStatusAsync(project, change));

    private Task SetStatusAsync(ProjectInfo project, Func<ProjectStatus, ProjectStatus> change)
    {
        project.Status = change(project.Status);
        return _statusUpdater.SaveStatusAsync(project);
    }

    private static async Task WithStateLockAsync(ProjectInfo project, Func<Task> action)
    {
        var stateLock = project.Process.StateLock;
        await stateLock.WaitAsync();
        try { await action(); }
        finally { stateLock.Release(); }
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the consumer, under the state lock, after everything already
    /// on the pipeline. Once the pipeline is closed nothing is queued, and it runs at once.
    /// </summary>
    private async Task InOrderAsync(ProjectInfo project, Func<Task> change)
    {
        EnsureConsumer(project);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (project.Process.Output.TryWrite(new PipelineItem.InOrder(change, done)))
            await done.Task;
        else
            await WithStateLockAsync(project, change);
    }

    /// <summary>Pushes the project's current status to every client.</summary>
    public async Task NotifyStatusChangedAsync(ProjectInfo project)
    {
        try
        {
            await _hubContext.Clients.All.StatusChanged(project.Status.Id, project.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting the status of project {ProjectId}", project.Status.Id);
        }
    }

    // ── Output ──

    private async Task ConsumeAsync(ProjectInfo project, ChannelReader<PipelineItem> items)
    {
        while (await items.WaitToReadAsync())
        {
            // Open for each burst, so nothing holds output.jsonl while the project is quiet
            await using var output = OpenOutput(project);
            while (items.TryRead(out var item))
                await (item switch
                {
                    PipelineItem.Line line => HandleOutputLineAsync(project, output, line.Json),
                    PipelineItem.Exited exited => HandleExitAsync(project, exited.Exit),
                    PipelineItem.InOrder inOrder => RunInOrderAsync(project, inOrder),
                    _ => throw new InvalidOperationException($"Unknown pipeline item {item}"),
                });
        }
    }

    private static async Task RunInOrderAsync(ProjectInfo project, PipelineItem.InOrder item)
    {
        try
        {
            await WithStateLockAsync(project, item.Change);
            item.Done.TrySetResult();
        }
        catch (Exception ex)
        {
            item.Done.TrySetException(ex);
        }
    }

    /// <summary>
    /// The process ended on its own: Stopped if it exited cleanly with its turn over, Error with its
    /// last stderr otherwise. A process the server killed changes nothing: whoever killed it decides.
    /// </summary>
    private async Task HandleExitAsync(ProjectInfo project, ProcessExit exit)
    {
        if (exit.Killed) return;

        var changed = false;
        try
        {
            await WithStateLockAsync(project, async () =>
            {
                // A later launch is already running; its own exit settles the state
                if (project.Process.ProcessId != 0) return;

                var finished = exit.ExitCode == 0 && project.Status.State is ProjectState.Idle or ProjectState.WaitingInput;
                await SetStatusAsync(project, status => status with
                {
                    State = finished ? ProjectState.Stopped : ProjectState.Error,
                    CurrentQuestion = null,
                    LastError = finished ? null : exit.Stderr ?? $"claude exited with code {exit.ExitCode}",
                    UpdatedAt = DateTime.UtcNow
                });
                changed = true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling the exit of project {ProjectId}", project.Status.Id);
        }

        if (changed) await NotifyStatusChangedAsync(project);
    }

    private StreamWriter? OpenOutput(ProjectInfo project)
    {
        var outputPath = Path.Combine(project.ProjectPath, ".godmode", "output.jsonl");
        try
        {
            var stream = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot open output.jsonl for project {ProjectId}; its output is not persisted", project.Status.Id);
            return null;
        }
    }

    private async Task HandleOutputLineAsync(ProjectInfo project, StreamWriter? output, string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return;
        var id = project.Status.Id;

        _logger.LogDebug("Claude output [{ProjectId}]: {Output}",
            id, jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine);

        var completed = false;
        var statusChanged = false;
        try
        {
            // 1. Persist, so a client subscribing from here on backfills this line
            if (output != null) await output.WriteLineAsync(jsonLine);

            // 2. State
            if (ParseClaudeOutput(jsonLine) is { } outputEvent)
                await WithStateLockAsync(project, async () =>
                {
                    var previous = project.Status.State;
                    statusChanged = await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent, jsonLine);
                    completed = previous != ProjectState.Idle && project.Status.State == ProjectState.Idle;
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling output for project {ProjectId}", id);
        }

        // 3. Broadcast the raw JSON to subscribed clients; the UI parses and renders it
        try
        {
            await _hubContext.Clients.Group($"project-{id}").OutputReceived(id, jsonLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting output for project {ProjectId}", id);
        }

        // 4. Every client hears the change, subscribed to this project's output or not
        if (statusChanged) await NotifyStatusChangedAsync(project);

        if (completed && OnProjectCompleted != null)
        {
            try { await OnProjectCompleted(id); }
            catch (Exception ex) { _logger.LogError(ex, "Error in OnProjectCompleted handler for project {ProjectId}", id); }
        }
    }

    /// <summary>
    /// Extracts the event type from raw JSON for logging purposes.
    /// </summary>
    internal static string? ExtractEventType(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                return typeElement.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Parses Claude's raw JSON output into an OutputEvent with properly extracted content.
    /// Null for a line that is not JSON or not an event type GodMode tracks.
    /// </summary>
    private OutputEvent? ParseClaudeOutput(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement))
                return null;

            var typeStr = typeElement.GetString();
            if (!Enum.TryParse<OutputEventType>(typeStr, ignoreCase: true, out var eventType))
                return null;

            var content = ExtractContent(root, eventType);
            var metadata = ExtractMetadata(root);

            return new OutputEvent(DateTime.UtcNow, eventType, content, metadata);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Output line is not JSON: {Line}", jsonLine);
            return null;
        }
    }

    /// <summary>
    /// Extracts the content from Claude's JSON based on event type.
    /// </summary>
    private static string ExtractContent(JsonElement root, OutputEventType eventType)
    {
        return eventType switch
        {
            OutputEventType.User => ExtractMessageContent(root),
            OutputEventType.Assistant => ExtractMessageContent(root),
            OutputEventType.Result => ExtractResultContent(root),
            OutputEventType.System => ExtractSystemContent(root),
            OutputEventType.Error => root.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "",
            _ => ""
        };
    }

    private static string ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return "";

        if (!message.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            return "";

        var firstContent = content[0];
        if (firstContent.TryGetProperty("text", out var text))
            return text.GetString() ?? "";

        return "";
    }

    private static string ExtractResultContent(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
            return result.GetString() ?? "";

        return "";
    }

    private static string ExtractSystemContent(JsonElement root)
    {
        if (root.TryGetProperty("subtype", out var subtype))
        {
            var subtypeStr = subtype.GetString() ?? "";
            if (root.TryGetProperty("session_id", out var sessionId))
                return $"{subtypeStr} (session: {sessionId.GetString()?[..8]}...)";
            return subtypeStr;
        }
        return "system";
    }

    private static Dictionary<string, object>? ExtractMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, object>();

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inputTokens))
                metadata["input_tokens"] = inputTokens.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var outputTokens))
                metadata["output_tokens"] = outputTokens.GetInt64();
        }

        if (root.TryGetProperty("total_cost_usd", out var cost))
            metadata["cost_usd"] = cost.GetDouble();

        if (root.TryGetProperty("duration_ms", out var duration))
            metadata["duration_ms"] = duration.GetInt64();

        if (root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String)
            metadata["subtype"] = subtype.GetString()!;

        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind is JsonValueKind.True or JsonValueKind.False)
            metadata["is_error"] = isError.GetBoolean();

        return metadata.Count > 0 ? metadata : null;
    }
}
