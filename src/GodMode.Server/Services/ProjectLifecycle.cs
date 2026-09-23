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
/// from output is deterministic. Per-project state lives in <see cref="ProjectInfo.Process"/>.
/// </summary>
public sealed class ProjectLifecycle
{
    private readonly IClaudeProcessManager _processManager;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ProjectLifecycle> _logger;

    /// <summary>Raised when output takes a project to Idle.</summary>
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

    public async Task StartAsync(ProjectInfo project, string initialPrompt,
        Dictionary<string, string>? environment, string[]? args)
    {
        var process = BeginLaunch(project);
        process.ProcessId = await _processManager.StartClaudeProcessAsync(
            project, initialPrompt, process.Cancellation!.Token, environment, args);
    }

    public async Task ResumeAsync(ProjectInfo project, Dictionary<string, string>? environment, string[]? args)
    {
        var process = BeginLaunch(project);
        process.ProcessId = await _processManager.ResumeClaudeProcessAsync(
            project, process.Cancellation!.Token, environment, args);
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
        process.EnsureConsumer(lines => ConsumeOutputAsync(project, lines));
        return process;
    }

    public bool IsRunning(ProjectInfo project) => _processManager.IsProcessRunning(project.Process.ProcessId);

    public Task StopAsync(ProjectInfo project) => _processManager.StopProcessAsync(project);

    /// <summary>Stops the process and lets the consumer finish, before the project is removed.</summary>
    public async Task CloseAsync(ProjectInfo project)
    {
        await StopAsync(project);
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

    // ── Output ──

    private async Task ConsumeOutputAsync(ProjectInfo project, ChannelReader<string> lines)
    {
        while (await lines.WaitToReadAsync())
        {
            // Open for each burst, so nothing holds output.jsonl while the project is quiet
            await using var output = OpenOutput(project);
            while (lines.TryRead(out var line))
                await HandleOutputLineAsync(project, output, line);
        }
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
        try
        {
            // 1. Persist, so a client subscribing from here on backfills this line
            if (output != null) await output.WriteLineAsync(jsonLine);

            // 2. State
            if (ParseClaudeOutput(jsonLine) is { } outputEvent)
                await WithStateLockAsync(project, async () =>
                {
                    var previous = project.Status.State;
                    await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent, jsonLine);
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

        return metadata.Count > 0 ? metadata : null;
    }
}
