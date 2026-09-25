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
/// for each, appends it to <c>output.jsonl</c>, updates state, then broadcasts it with the offset after
/// it (see <see cref="OutputLog"/>), so state derived
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

    private volatile bool _shuttingDown;

    /// <summary>Held from a launch's check for shutdown until its process is started: see <see cref="BeginShutdown"/>.</summary>
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    /// <summary>How long <see cref="BeginShutdown"/> waits for a launch under way to have started its process.</summary>
    private static readonly TimeSpan LaunchGateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Raised, on the project's consumer, when output takes a project to Idle. A handler must not wait for a Stop.</summary>
    public event Func<string, Task>? OnProjectCompleted;

    /// <summary>Raised after every <see cref="NotifyStatusChangedAsync"/>, with its project, once clients have the status.</summary>
    public event Func<ProjectInfo, Task>? StatusNotified;

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

    public Task StartAsync(ProjectInfo project, string initialPrompt, ClaudeLaunchSpec launch) =>
        LaunchAsync(project, cancel => _processManager.StartClaudeProcessAsync(project, initialPrompt, cancel, launch.Environment, launch.Args));

    public Task ResumeAsync(ProjectInfo project, ClaudeLaunchSpec launch) =>
        LaunchAsync(project, cancel => _processManager.ResumeClaudeProcessAsync(project, cancel, launch.Environment, launch.Args));

    /// <summary>
    /// Starts a process, unless the server is stopping (<see cref="ServerStoppingException"/>): one
    /// started after the shutdown stopped the projects would outlive the server.
    /// </summary>
    private async Task LaunchAsync(ProjectInfo project, Func<CancellationToken, Task<int>> launch)
    {
        await _launchGate.WaitAsync();
        try
        {
            if (_shuttingDown) throw new ServerStoppingException();
            var process = BeginLaunch(project);
            await launch(process.Cancellation!.Token);
        }
        finally
        {
            _launchGate.Release();
        }
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
        process.Stopping = false;
        EnsureConsumer(project);
        return process;
    }

    private void EnsureConsumer(ProjectInfo project) =>
        project.Process.EnsureConsumer(items => ConsumeAsync(project, items));

    /// <summary>
    /// The server is stopping: from now on a process that exits on its own went with it (a Ctrl+C
    /// reaches claude too) and is Stopped, keeping its question, rather than failed, and nothing is
    /// launched. A launch under way is waited for, so its process is there for the shutdown to stop.
    /// </summary>
    public void BeginShutdown()
    {
        var entered = _launchGate.Wait(LaunchGateTimeout);
        _shuttingDown = true;
        if (entered) _launchGate.Release();
    }

    public bool ShuttingDown => _shuttingDown;

    public bool IsRunning(ProjectInfo project) => _processManager.IsProcessRunning(project.Process.ProcessId);

    /// <summary>How long a stop gives claude to exit once interrupted.</summary>
    public TimeSpan StopGracePeriod => _processManager.StopGracePeriod;

    /// <summary>
    /// Waits until the project's process is either running or gone: a launch claimed has its process
    /// or has failed, and an exit is handled (a fresh session that takes its place included). Then
    /// <see cref="IsRunning"/> says whether it has one. Called under the project's resume lock.
    /// </summary>
    public async Task SettleAsync(ProjectInfo project)
    {
        await project.Process.WhenLaunched();
        await _processManager.SettleAsync(project);
    }

    /// <summary>
    /// Stops the process, gracefully first (<see cref="IClaudeProcessManager.StopProcessAsync"/>,
    /// within <paramref name="grace"/> when given), then marks the project Stopped once every line it
    /// wrote has been handled, so a result still queued cannot turn Stopped back into Idle. Its output
    /// and exit are still handled, but its answer to the interrupt and its exit change no state. A
    /// shutdown passes what the project was doing (<see cref="ActiveState"/>), for the next start to
    /// carry on with; a stop by the user passes nothing, and its project is not resumed.
    /// </summary>
    public async Task StopAsync(ProjectInfo project, ProjectState? stateAtShutdown = null, TimeSpan? grace = null)
    {
        project.Process.Stopping = true;
        await _processManager.StopProcessAsync(project, grace);
        project.Process.DenyAllPending(StoppedMessage);
        await InOrderAsync(project, () => SetStatusAsync(project, status => WithoutPending(status) with
        {
            State = ProjectState.Stopped,
            StateAtShutdown = stateAtShutdown,
            UpdatedAt = DateTime.UtcNow
        }));
    }

    /// <summary>The state itself when it is one a restart carries on with (claude was working, or waiting on the user); null otherwise.</summary>
    public static ProjectState? ActiveState(ProjectState state) =>
        state is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission ? state : null;

    /// <summary>
    /// A Ctrl+C on a server run in a terminal reaches claude too, and claude can exit, and its exit
    /// be handled (Error, or Stopped without its question), before the server's shutdown begins.
    /// An exit on its own no more than <paramref name="window"/> before the shutdown, with nothing
    /// changed since, is taken for that: the project is Stopped as the shutdown would have left it,
    /// its question and what it was doing restored. True when it was.
    /// </summary>
    public async Task<bool> UndoExitBeforeShutdownAsync(ProjectInfo project, TimeSpan window)
    {
        var undone = false;
        await WithStateLockAsync(project, async () =>
        {
            if (project.Process.LastExit is not { } exit || !ReferenceEquals(exit.After, project.Status)
                || DateTime.UtcNow - exit.At > window || ActiveState(exit.Before.State) is not { } active)
                return;
            project.Process.LastExit = null;
            await SetStatusAsync(project, status => status with
            {
                State = ProjectState.Stopped,
                StateAtShutdown = active,
                CurrentQuestion = exit.Before.CurrentQuestion,
                LastError = null,
                UpdatedAt = DateTime.UtcNow
            });
            undone = true;
        });
        return undone;
    }

    /// <summary>What a permission prompt still waiting when its session ends is answered with.</summary>
    private const string StoppedMessage = "The GodMode session stopped before the user answered.";

    /// <summary>
    /// The status without its pending request: none survives the process it came from. A question
    /// keeps its text in <see cref="ProjectStatus.CurrentQuestion"/>, as a question in plain text does.
    /// </summary>
    private static ProjectStatus WithoutPending(ProjectStatus status) =>
        status with { PendingPermission = null, PendingQuestion = null };

    /// <summary>
    /// Shows the oldest permission prompt claude is waiting on in the status: WaitingPermission for
    /// a tool call, WaitingInput for an AskUserQuestion. With none left, a project that was waiting
    /// on one is Running again, as claude is. Pushes the status when it changed.
    /// </summary>
    public async Task ShowPendingAsync(ProjectInfo project)
    {
        var changed = false;
        await WithStateLockAsync(project, async () =>
        {
            var before = project.Status;
            var after = project.Process.OldestPending switch
            {
                { Permission: { } permission } => before with
                {
                    State = ProjectState.WaitingPermission,
                    PendingPermission = permission,
                    PendingQuestion = null,
                    CurrentQuestion = null,
                },
                { Question: { } question } => before with
                {
                    State = ProjectState.WaitingInput,
                    PendingPermission = null,
                    PendingQuestion = question,
                    CurrentQuestion = question.Questions[0].Question,
                    QuestionAt = question.RequestedAt,
                },
                _ when before.PendingPermission != null || before.PendingQuestion != null => WithoutPending(before) with
                {
                    State = before.State is ProjectState.WaitingPermission or ProjectState.WaitingInput ? ProjectState.Running : before.State,
                    CurrentQuestion = before.PendingQuestion != null ? null : before.CurrentQuestion,
                },
                _ => before,
            };
            if (after == before) return;
            await SetStatusAsync(project, status => after with { UpdatedAt = DateTime.UtcNow });
            changed = true;
        });
        if (changed) await NotifyStatusChangedAsync(project);
    }

    /// <summary>
    /// Sends user input and marks the project Running, under the state lock: a reply that lands
    /// before Running is set waits for it, so Running can never overwrite the reply's state. A
    /// reply means the user has seen the last result. Returns the process the input went to.
    /// </summary>
    public async Task<int> SendInputAsync(ProjectInfo project, string input)
    {
        var sentTo = 0;
        await WithStateLockAsync(project, async () =>
        {
            sentTo = project.Process.ProcessId;
            await _processManager.SendInputAsync(project, input);
            var now = DateTime.UtcNow;
            await SetStatusAsync(project, status => status with
            {
                State = ProjectState.Running,
                CurrentQuestion = null,
                SeenAt = now,
                UpdatedAt = now
            });
        });
        return sentTo;
    }

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
    /// Runs <paramref name="change"/> on the consumer, under the state lock unless told otherwise,
    /// after everything already on the pipeline. Once the pipeline is closed nothing is queued, and
    /// it runs at once.
    /// </summary>
    private async Task InOrderAsync(ProjectInfo project, Func<Task> change, bool underStateLock = true)
    {
        EnsureConsumer(project);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (project.Process.Output.TryWrite(new PipelineItem.InOrder(change, done, underStateLock)))
            await done.Task;
        else
            await (underStateLock ? WithStateLockAsync(project, change) : change());
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

        if (StatusNotified == null) return;
        try { await StatusNotified(project); }
        catch (Exception ex) { _logger.LogError(ex, "Error in StatusNotified handler for project {ProjectId}", project.Status.Id); }
    }

    // ── Output ──

    /// <summary>The SignalR group of the connections that get a project's live output.</summary>
    public static string OutputGroup(string projectId) => $"project-{projectId}";

    /// <summary>
    /// Replays output.jsonl to the connection from <paramref name="fromOffset"/> (see
    /// <see cref="OutputLog.StartAsync"/>), then adds it to the project's live group. The
    /// connection is out of the group while the file is read; the last read happens on the
    /// consumer, between two lines, and the join with it, so every line is either in the replay or
    /// broadcast to the connection afterwards, never both and never neither. Each batch and the
    /// complete carry <paramref name="subscriptionId"/> and the file's generation; a positive offset
    /// in a generation other than <paramref name="generation"/> is not in this file, and all of it is replayed.
    /// </summary>
    public async Task SubscribeAsync(ProjectInfo project, long fromOffset, string subscriptionId, string? generation, string connectionId)
    {
        var id = project.Status.Id;
        var replay = new Replay(_hubContext.Clients.Client(connectionId), id, subscriptionId,
            await OutputLog.GenerationAsync(project.ProjectPath));
        await _hubContext.Groups.RemoveFromGroupAsync(connectionId, OutputGroup(id));

        var from = fromOffset > 0 && generation != replay.Generation ? 0 : fromOffset;
        var offset = await ReplayAsync(project, replay, await OutputLog.StartAsync(project.ProjectPath, from));
        await InOrderAsync(project, async () =>
        {
            offset = await ReplayAsync(project, replay, offset);
            await _hubContext.Groups.AddToGroupAsync(connectionId, OutputGroup(id));
            await replay.Client.OutputReplayComplete(id, subscriptionId, replay.Generation, offset);
        }, underStateLock: false);

        _logger.LogInformation("Replayed output of project {ProjectId} to {ConnectionId} for {SubscriptionId} from {FromOffset} to {Offset}",
            id, connectionId, subscriptionId, from, offset);
    }

    /// <summary>A subscription's replay: who it goes to, and what its batches and complete carry.</summary>
    private sealed record Replay(IProjectHubClient Client, string ProjectId, string SubscriptionId, string Generation);

    /// <summary>Sends the complete lines from <paramref name="offset"/> in batches; returns the offset after the last.</summary>
    private static async Task<long> ReplayAsync(ProjectInfo project, Replay replay, long offset)
    {
        await foreach (var batch in OutputLog.ReadBatchesAsync(project.ProjectPath, offset))
        {
            await replay.Client.OutputBatch(replay.ProjectId, replay.SubscriptionId, replay.Generation, offset, batch);
            offset = batch[^1].Offset;
        }
        return offset;
    }

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
            await (item.UnderStateLock ? WithStateLockAsync(project, item.Change) : item.Change());
            item.Done.TrySetResult();
        }
        catch (Exception ex)
        {
            item.Done.TrySetException(ex);
        }
    }

    /// <summary>
    /// The process ended on its own: Stopped if it exited cleanly with its turn over, Error with its
    /// last stderr otherwise. During shutdown it is Stopped, question kept. A process the server
    /// stopped changes nothing: whoever stopped it decides.
    /// </summary>
    private async Task HandleExitAsync(ProjectInfo project, ProcessExit exit)
    {
        // A reply waiting for this launch's session to start waits no longer
        if (project.Process.ProcessId == 0)
            project.Process.SessionEnded(exit.Stopped
                ? "claude was stopped before it started its session"
                : $"claude exited before it started its session: {exit.Stderr ?? $"exit code {exit.ExitCode}"}");

        if (exit.Stopped) return;

        var changed = false;
        try
        {
            await WithStateLockAsync(project, async () =>
            {
                // A later launch is already running; its own exit settles the state
                if (project.Process.ProcessId != 0) return;

                // Its calls to the MCP endpoint went with it; nothing can answer claude any more
                project.Process.DenyAllPending(StoppedMessage);

                var shuttingDown = _shuttingDown;
                var before = project.Status;
                var finished = shuttingDown || exit.ExitCode == 0 && before.State is ProjectState.Idle or ProjectState.WaitingInput;
                await SetStatusAsync(project, status => WithoutPending(status) with
                {
                    State = finished ? ProjectState.Stopped : ProjectState.Error,
                    CurrentQuestion = shuttingDown ? status.CurrentQuestion : null,
                    LastError = finished ? null : exit.Stderr ?? $"claude exited with code {exit.ExitCode}",
                    UpdatedAt = DateTime.UtcNow
                });
                // A shutdown that follows at once may take it back: see UndoExitBeforeShutdownAsync
                project.Process.LastExit = shuttingDown ? null : new ExitOnItsOwn(DateTime.UtcNow, before, project.Status);
                changed = true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling the exit of project {ProjectId}", project.Status.Id);
        }

        if (changed) await NotifyStatusChangedAsync(project);
    }

    private OutputLog.Writer? OpenOutput(ProjectInfo project)
    {
        try
        {
            return OutputLog.OpenWriter(project.ProjectPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot open output.jsonl for project {ProjectId}; its output is not persisted", project.Status.Id);
            return null;
        }
    }

    private async Task HandleOutputLineAsync(ProjectInfo project, OutputLog.Writer? output, string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return;
        var id = project.Status.Id;

        _logger.LogDebug("Claude output [{ProjectId}]: {Output}",
            id, jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine);

        var completed = false;
        var statusChanged = false;
        var offset = project.Status.OutputOffset;
        try
        {
            // 1. Persist, so a client subscribing from here on backfills this line
            if (output != null) offset = await output.AppendAsync(jsonLine);

            // 2. State
            await WithStateLockAsync(project, async () =>
            {
                // In memory only: status.json carries it when something else changes, and recovery reads it from the file
                project.Status = project.Status with { OutputOffset = offset };
                if (ParseClaudeOutput(jsonLine) is not { } outputEvent) return;
                var previous = project.Status.State;
                statusChanged = await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent, jsonLine);
                completed = previous != ProjectState.Idle && project.Status.State == ProjectState.Idle;
                if (StatusUpdater.IsSessionStart(outputEvent)) project.Process.SessionStarted();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling output for project {ProjectId}", id);
        }

        // 3. Broadcast the raw JSON to subscribed clients; the UI parses and renders it
        try
        {
            await _hubContext.Clients.Group(OutputGroup(id)).OutputReceived(id, offset, jsonLine);
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

        // Every line carries it; only system/init's is read, so only system lines keep it
        if (root.TryGetProperty("type", out var type) && type.ValueEquals("system")
            && root.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String)
            metadata[StatusUpdater.SessionIdKey] = sessionId.GetString()!;

        return metadata.Count > 0 ? metadata : null;
    }
}
