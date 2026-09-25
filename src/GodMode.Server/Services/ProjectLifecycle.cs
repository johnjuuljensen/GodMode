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
        _ = project.Process.EnsureConsumer(items => ConsumeAsync(project, items));

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
    /// and exit are still handled, but its answer to the interrupt and its exit change no state. The
    /// permission prompts it waits on are denied first: killing it would drop their calls, whose
    /// cleanup would show it Running again. A shutdown passes the <paramref name="shutdown"/> it
    /// persisted first (<see cref="MarkForShutdownAsync"/>), and keeps its marker, unless a stop by
    /// the user cleared it meanwhile; a stop by the user passes nothing and clears it, so its project
    /// is not resumed.
    /// </summary>
    public async Task StopAsync(ProjectInfo project, ShutdownMarker? shutdown = null, TimeSpan? grace = null)
    {
        project.Process.Stopping = true;
        project.Process.DenyAllPending(StoppedMessage);
        await _processManager.StopProcessAsync(project, grace);
        // Any it asked while it was being stopped
        project.Process.DenyAllPending(StoppedMessage);
        await InOrderAsync(project, () => SetStatusAsync(project, status => WithoutPending(status) with
        {
            State = ProjectState.Stopped,
            StateAtShutdown = shutdown == null ? null : status.StateAtShutdown,
            // What the user was asked, whatever claude did with the deny before it stopped
            CurrentQuestion = shutdown is { Question: { } question } && status.StateAtShutdown == ProjectState.WaitingInput
                ? question
                : status.CurrentQuestion,
            UpdatedAt = DateTime.UtcNow
        }));
    }

    /// <summary>The state itself when it is one a restart carries on with (claude was working, or waiting on the user); null otherwise.</summary>
    public static ProjectState? ActiveState(ProjectState state) =>
        state is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission ? state : null;

    /// <summary>
    /// What the project was doing as the shutdown began, for the next start to carry on with: its
    /// <see cref="ActiveState"/>, and the question it waited on. Nothing for a project a stop by the
    /// user is stopping: it is not resumed.
    /// </summary>
    public static ShutdownMarker MarkerOf(ProjectInfo project)
    {
        var status = project.Status;
        var active = project.Process.Stopping ? null : ActiveState(status.State);
        return new ShutdownMarker(active, active == ProjectState.WaitingInput ? status.CurrentQuestion : null);
    }

    /// <summary>
    /// Persists <paramref name="marker"/> as the project's <see cref="ProjectStatus.StateAtShutdown"/>,
    /// before the shutdown stops it: nothing that happens to it from here on (a result, its exit, a
    /// server killed before the stop is done) takes the marker away, except a stop by the user that
    /// began since, which leaves it unmarked.
    /// </summary>
    public Task MarkForShutdownAsync(ProjectInfo project, ShutdownMarker marker) =>
        WithStateLockAsync(project, async () =>
        {
            var state = project.Process.Stopping ? null : marker.State;
            if (project.Status.StateAtShutdown != state)
                await SetStatusAsync(project, status => status with { StateAtShutdown = state });
        });

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
    /// on one is Running again, as claude is, unless it is being stopped: the stop decides its state
    /// then, and a question keeps its text for a shutdown's marker. Pushes the status when it changed.
    /// </summary>
    public async Task ShowPendingAsync(ProjectInfo project)
    {
        var changed = false;
        await WithStateLockAsync(project, async () =>
        {
            var before = project.Status;
            var after = WithPending(before, project.Process.OldestPending, project.Process.Stopping);
            if (after == before) return;
            await SetStatusAsync(project, _ => after with { UpdatedAt = DateTime.UtcNow });
            changed = true;
        });
        if (changed) await NotifyStatusChangedAsync(project);
    }

    /// <summary>The status showing <paramref name="oldest"/>, the permission prompt claude waits on: see <see cref="ShowPendingAsync"/>.</summary>
    private static ProjectStatus WithPending(ProjectStatus before, PendingRequest? oldest, bool stopping) => oldest switch
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
        _ when stopping => before,
        _ when before.PendingPermission != null || before.PendingQuestion != null => WithoutPending(before) with
        {
            State = before.State is ProjectState.WaitingPermission or ProjectState.WaitingInput ? ProjectState.Running : before.State,
            CurrentQuestion = before.PendingQuestion != null ? null : before.CurrentQuestion,
        },
        _ => before,
    };

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
        return SaveStatusAsync(project);
    }

    /// <summary>
    /// Saves the status. A save that fails (status.json cannot be replaced) does not fail the change:
    /// it is logged, the status stays as changed in memory and is pushed as any other, and it is
    /// saved with the next change.
    /// </summary>
    private async Task SaveStatusAsync(ProjectInfo project)
    {
        try
        {
            await _statusUpdater.SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the status of project {ProjectId}; it is saved with its next change", project.Status.Id);
        }
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
    /// it runs at once. When no consumer can run it, it fails rather than waits
    /// (<see cref="ProjectProcess.RunInOrderAsync"/>).
    /// </summary>
    private async Task InOrderAsync(ProjectInfo project, Func<Task> change, bool underStateLock = true)
    {
        var item = new PipelineItem.InOrder(change, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), underStateLock);
        if (!await project.Process.RunInOrderAsync(item, items => ConsumeAsync(project, items)))
            await (underStateLock ? WithStateLockAsync(project, change) : change());
    }

    /// <summary>
    /// Pushes the project's current status to every client, then raises <see cref="StatusNotified"/>.
    /// The push is started, not waited for (<see cref="ProjectProcess.Sends"/>).
    /// </summary>
    public async Task NotifyStatusChangedAsync(ProjectInfo project)
    {
        var (id, status) = (project.Status.Id, project.Status);
        await project.Process.Sends.SendAsync(() => _hubContext.Clients.All.StatusChanged(id, status),
            ex => _logger.LogError(ex, "Error broadcasting the status of project {ProjectId}", id));

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
        // On the consumer, the sends are started in order and not waited for: a subscriber that
        // reads slowly holds up its own subscribe, not the project's output
        var sent = new List<Task>();
        await InOrderAsync(project, async () =>
        {
            offset = await ReplayAsync(project, replay, offset, sent);
            await _hubContext.Groups.AddToGroupAsync(connectionId, OutputGroup(id));
            sent.Add(replay.Client.OutputReplayComplete(id, subscriptionId, replay.Generation, offset));
        }, underStateLock: false);
        await Task.WhenAll(sent);

        _logger.LogInformation("Replayed output of project {ProjectId} to {ConnectionId} for {SubscriptionId} from {FromOffset} to {Offset}",
            id, connectionId, subscriptionId, from, offset);
    }

    /// <summary>A subscription's replay: who it goes to, and what its batches and complete carry.</summary>
    private sealed record Replay(IProjectHubClient Client, string ProjectId, string SubscriptionId, string Generation);

    /// <summary>
    /// Sends the complete lines from <paramref name="offset"/> in batches; returns the offset after
    /// the last. With <paramref name="sent"/>, each batch is started and added to it, not waited for.
    /// </summary>
    private static async Task<long> ReplayAsync(ProjectInfo project, Replay replay, long offset, List<Task>? sent = null)
    {
        await foreach (var batch in OutputLog.ReadBatchesAsync(project.ProjectPath, offset))
        {
            var send = replay.Client.OutputBatch(replay.ProjectId, replay.SubscriptionId, replay.Generation, offset, batch);
            if (sent != null) sent.Add(send);
            else await send;
            offset = batch[^1].Offset;
        }
        return offset;
    }

    /// <summary>
    /// The project's one consumer. Each item's failure is its own: it is logged, and the consumer
    /// goes on with the next. One that faults all the same is logged, and replaced
    /// (<see cref="ProjectProcess.EnsureConsumer"/>).
    /// </summary>
    private async Task ConsumeAsync(ProjectInfo project, ChannelReader<PipelineItem> items)
    {
        try
        {
            while (await items.WaitToReadAsync())
            {
                // Open for each burst, so nothing holds output.jsonl while the project is quiet
                var output = new BurstOutput(this, project);
                try
                {
                    while (items.TryRead(out var item))
                    {
                        try
                        {
                            await (item switch
                            {
                                PipelineItem.Line line => HandleOutputLineAsync(project, output, line),
                                PipelineItem.Exited exited => HandleExitAsync(project, exited.Exit),
                                PipelineItem.InOrder inOrder => RunInOrderAsync(project, inOrder),
                                _ => throw new InvalidOperationException($"Unknown pipeline item {item}"),
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling {Item} for project {ProjectId}", item.GetType().Name, project.Status.Id);
                        }
                    }
                }
                finally
                {
                    await output.CloseAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The output consumer of project {ProjectId} failed; it is started again", project.Status.Id);
            throw;
        }
    }

    /// <summary>
    /// output.jsonl for one burst of output. A line whose append fails is tried once more on the file
    /// opened again, cut back to where the last whole line ended, so every offset stays the file's.
    /// A line that still cannot be appended is not persisted, and the next line opens the file again.
    /// </summary>
    private sealed class BurstOutput(ProjectLifecycle lifecycle, ProjectInfo project)
    {
        private OutputLog.Writer? _writer;
        private bool _opened;
        private long? _end;

        /// <summary>Appends the line; the offset after it, or null when it could not be persisted.</summary>
        public async Task<long?> AppendAsync(string line)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (Open() is not { } writer) return null;
                var end = writer.Offset;
                try
                {
                    return await writer.AppendAsync(line);
                }
                catch (Exception ex)
                {
                    lifecycle._logger.LogError(ex, "Could not append to output.jsonl of project {ProjectId}{Retry}",
                        project.Status.Id, attempt == 1 ? "; trying again on the file opened again" : "; the line is not persisted");
                    _end = end;
                    await CloseAsync();
                }
            }
            return null;
        }

        private OutputLog.Writer? Open()
        {
            if (_opened) return _writer;
            _opened = true;
            try
            {
                _writer = OutputLog.OpenWriter(project.ProjectPath, _end, lifecycle.WrapOutput);
                _end = null;
                return _writer;
            }
            catch (Exception ex)
            {
                lifecycle._logger.LogError(ex, "Cannot open output.jsonl for project {ProjectId}; its output is not persisted", project.Status.Id);
                return null;
            }
        }

        /// <summary>Closes the file; the next append opens it again. A close that fails (a flush that cannot be written) is logged.</summary>
        public async Task CloseAsync()
        {
            var writer = _writer;
            _writer = null;
            _opened = false;
            if (writer == null) return;
            try { await writer.DisposeAsync(); }
            catch (Exception ex) { lifecycle._logger.LogError(ex, "Could not close output.jsonl of project {ProjectId}", project.Status.Id); }
        }
    }

    /// <summary>Tests only: a stream put between the consumer's writer and output.jsonl (see <see cref="OutputLog.OpenWriter"/>).</summary>
    internal Func<Stream, Stream>? WrapOutput { get; set; }

    private static async Task RunInOrderAsync(ProjectInfo project, PipelineItem.InOrder item)
    {
        // Given up on already (see ProjectProcess.RunInOrderAsync): it is not run late
        if (item.Done.Task.IsCompleted) return;
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

    /// <summary>What a permission prompt still listed when claude ends its turn is answered with: claude waits on it no more.</summary>
    private const string TurnEndedMessage = "claude ended its turn before the user answered.";

    /// <summary>
    /// One line of claude's output: persisted, then the state it implies, then broadcast with the
    /// offset after it. A line that could not be persisted is not broadcast: its offset would be
    /// the previous line's, and a client drops it. A status that changed is pushed, saved or not.
    /// </summary>
    private async Task HandleOutputLineAsync(ProjectInfo project, BurstOutput output, PipelineItem.Line line)
    {
        var jsonLine = line.Json;
        if (string.IsNullOrWhiteSpace(jsonLine)) return;
        var id = project.Status.Id;

        _logger.LogDebug("Claude output [{ProjectId}]: {Output}",
            id, jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine);

        var completed = false;
        var statusChanged = false;
        long? offset = null;
        try
        {
            // 1. Persist, so a client subscribing from here on backfills this line
            offset = await output.AppendAsync(jsonLine);

            // 2. State
            await WithStateLockAsync(project, async () =>
            {
                // In memory only: status.json carries it when something else changes, and recovery reads it from the file
                if (offset is { } persisted) project.Status = project.Status with { OutputOffset = persisted };
                if (ParseClaudeOutput(jsonLine) is not { } outputEvent) return;
                var previous = project.Status.State;
                statusChanged = await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent, jsonLine);
                if (outputEvent.Type == OutputEventType.Result) statusChanged |= ClearPendingOfEndedTurn(project, line);
                if (statusChanged) await SaveStatusAsync(project);
                completed = previous != ProjectState.Idle && project.Status.State == ProjectState.Idle;
                if (StatusUpdater.IsSessionStart(outputEvent)) project.Process.SessionStarted();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling output for project {ProjectId}", id);
        }

        // 3. Broadcast the raw JSON to subscribed clients; the UI parses and renders it
        if (offset is { } after)
            await project.Process.Sends.SendAsync(() => _hubContext.Clients.Group(OutputGroup(id)).OutputReceived(id, after, jsonLine),
                ex => _logger.LogError(ex, "Error broadcasting output for project {ProjectId}", id));

        // 4. Every client hears the change, subscribed to this project's output or not
        if (statusChanged) await NotifyStatusChangedAsync(project);

        if (completed && OnProjectCompleted != null)
        {
            try { await OnProjectCompleted(id); }
            catch (Exception ex) { _logger.LogError(ex, "Error in OnProjectCompleted handler for project {ProjectId}", id); }
        }
    }

    /// <summary>
    /// A <c>result</c> ends the turn: claude waits on no permission prompt that had arrived before
    /// it was read, so one still listed (its registration failed half-way) is denied and taken out
    /// of the status, and the next reply reaches claude. True when the status changed.
    /// </summary>
    private bool ClearPendingOfEndedTurn(ProjectInfo project, PipelineItem.Line line)
    {
        if (project.Process.DenyPendingUpTo(line.PendingIssued, TurnEndedMessage) > 0)
            _logger.LogWarning("Project {ProjectId} ended its turn with a permission prompt still listed; it is withdrawn", project.Status.Id);
        var before = project.Status;
        project.Status = project.Process.OldestPending is { } later ? WithPending(before, later, stopping: false) : WithoutPending(before);
        return project.Status != before;
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

        // A user message claude echoes (--replay-user-messages) as it takes it; a tool result is a user line without it
        if (root.TryGetProperty("isReplay", out var isReplay) && isReplay.ValueKind == JsonValueKind.True)
            metadata[StatusUpdater.IsReplayKey] = true;

        // Every line carries it; only system/init's is read, so only system lines keep it
        if (root.TryGetProperty("type", out var type) && type.ValueEquals("system")
            && root.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String)
            metadata[StatusUpdater.SessionIdKey] = sessionId.GetString()!;

        return metadata.Count > 0 ? metadata : null;
    }
}
