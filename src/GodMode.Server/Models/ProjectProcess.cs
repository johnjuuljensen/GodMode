using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GodMode.Server.Models;

/// <summary>
/// A project's process state for as long as the server tracks the project: the pipeline its
/// output flows through, the locks that order what happens to it, and the process itself.
/// <see cref="Services.ProjectLifecycle"/> drives it; nothing else reads the pipeline.
/// </summary>
public sealed class ProjectProcess
{
    private readonly Channel<PipelineItem> _output = Channel.CreateUnbounded<PipelineItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private int _processId;

    /// <summary>
    /// Every line claude writes to stdout, plus the stderr errors surfaced to the UI, in the order
    /// they arrived, then the process's exit. The process's pipe handlers only write here; the one
    /// consumer does the rest.
    /// </summary>
    public ChannelWriter<PipelineItem> Output => _output.Writer;

    /// <summary>Serialises writes to claude's stdin, so two sends cannot mix their JSON lines.</summary>
    public SemaphoreSlim StdinLock { get; } = new(1, 1);

    /// <summary>
    /// Held by whoever changes <see cref="ProjectInfo.Status"/>: the consumer for state derived from
    /// output, and the project operations (send, stop, resume) for theirs.
    /// </summary>
    public SemaphoreSlim StateLock { get; } = new(1, 1);

    /// <summary>The live claude process, 0 when there is none. Kept by the process manager.</summary>
    public int ProcessId
    {
        get => Volatile.Read(ref _processId);
        set => Volatile.Write(ref _processId, value);
    }

    /// <summary>Clears <see cref="ProcessId"/> if it is still <paramref name="processId"/>, not a later launch.</summary>
    public void ClearProcessId(int processId) => Interlocked.CompareExchange(ref _processId, 0, processId);

    public CancellationTokenSource? Cancellation { get; set; }

    private TaskCompletionSource? _launching;

    /// <summary>
    /// A launch is in flight: claimed, and neither its process id nor its failure recorded yet. A
    /// claim does not take the project's Running for a stale one while this is set.
    /// </summary>
    public bool Launching { get { lock (_gate) return _launching != null; } }

    /// <summary>Marks a launch in flight until <see cref="EndLaunching"/>; false when one already is.</summary>
    public bool BeginLaunching()
    {
        lock (_gate)
        {
            if (_launching != null) return false;
            _launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>The launch in flight has its process id, or failed.</summary>
    public void EndLaunching()
    {
        TaskCompletionSource? launching;
        lock (_gate)
        {
            launching = _launching;
            _launching = null;
        }
        launching?.TrySetResult();
    }

    /// <summary>Completes when no launch is in flight.</summary>
    public Task WhenLaunched() { lock (_gate) return _launching?.Task ?? Task.CompletedTask; }

    private volatile bool _stopping;

    /// <summary>
    /// A stop has begun, and claude has been, or is being, interrupted, until the next launch. Its
    /// answer to the interrupt, a turn ended in error, is not the session failing: the stop decides
    /// the project's state.
    /// </summary>
    public bool Stopping
    {
        get => _stopping;
        set => _stopping = value;
    }

    /// <summary>
    /// The most recent assistant text content block seen on the stream, used by
    /// the deterministic question detector to decide (on <c>result</c>) whether
    /// the turn ended with a question. Reset when a new turn starts.
    /// In-memory only; not persisted. Only the consumer touches it.
    /// </summary>
    public string? LastAssistantText { get; set; }

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

    /// <summary>The permission prompts claude is waiting on, by request id.</summary>
    public void AddPending(PendingRequest request) => _pending[request.Id] = request;

    /// <summary>The oldest permission prompt claude is waiting on, which the status shows; null for none.</summary>
    public PendingRequest? OldestPending => _pending.Values.MinBy(r => r.Sequence);

    public PendingRequest? FindPending(string requestId) => _pending.GetValueOrDefault(requestId);

    /// <summary>
    /// Answers the request, unless it was answered already or its caller gave up; true if this call
    /// took it off the list.
    /// </summary>
    public bool CompletePending(PendingRequest request, PermissionPromptResult result)
    {
        if (!_pending.TryRemove(new KeyValuePair<string, PendingRequest>(request.Id, request))) return false;
        request.Completion.TrySetResult(result);
        return true;
    }

    /// <summary>Denies every waiting request: the process they came from is gone or going.</summary>
    public void DenyAllPending(string message)
    {
        foreach (var request in _pending.Values)
            CompletePending(request, PermissionPromptResult.Deny(message));
    }

    private TaskCompletionSource<int>? _sessionStart;

    /// <summary>
    /// Completes at the next <c>system/init</c> the consumer handles, with the process running then:
    /// claude has started its session. Fails, with why, when the process exits first.
    /// </summary>
    public Task<int> NextSessionStart()
    {
        lock (_gate)
            return (_sessionStart ??= new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    /// <summary>claude reported its session started: see <see cref="NextSessionStart"/>.</summary>
    public void SessionStarted() => TakeSessionStart()?.TrySetResult(ProcessId);

    /// <summary>The process exited, with no later launch running: see <see cref="NextSessionStart"/>.</summary>
    public void SessionEnded(string reason) => TakeSessionStart()?.TrySetException(new InvalidOperationException(reason));

    private TaskCompletionSource<int>? TakeSessionStart()
    {
        lock (_gate)
        {
            var sessionStart = _sessionStart;
            _sessionStart = null;
            return sessionStart;
        }
    }

    /// <summary>
    /// The last time the process ended on its own outside a shutdown, with the status before and
    /// after. Only the consumer sets it, under the state lock.
    /// </summary>
    public ExitOnItsOwn? LastExit { get; set; }

    /// <summary>
    /// Held by whatever launches or stops the project's process (create, resume, a reply that
    /// resumes, stop, delete, the start carrying on after a restart), so one launch happens at a time
    /// and a stop comes before a launch or after it, never in the middle of one.
    /// </summary>
    public SemaphoreSlim ResumeLock { get; } = new(1, 1);

    private Task? _consumer;

    /// <summary>Starts the one consumer of <see cref="Output"/>, unless it is already running.</summary>
    public void EnsureConsumer(Func<ChannelReader<PipelineItem>, Task> consume)
    {
        lock (_gate)
            _consumer ??= Task.Run(() => consume(_output.Reader));
    }

    /// <summary>Closes the pipeline and waits for the consumer to finish the items already in it.</summary>
    public async Task CloseAsync()
    {
        _output.Writer.TryComplete();
        Task? consumer;
        lock (_gate) consumer = _consumer;
        if (consumer != null) await consumer;
    }
}

/// <summary>One item on a project's output pipeline, handled by its consumer in the order written.</summary>
public abstract record PipelineItem
{
    /// <summary>A line claude wrote to stdout, or a stderr error line surfaced to the UI.</summary>
    public sealed record Line(string Json) : PipelineItem;

    /// <summary>The process ended. Written after both of its pipes closed, so after every line it wrote.</summary>
    public sealed record Exited(ProcessExit Exit) : PipelineItem;

    /// <summary>
    /// A change that must come after everything queued before it, under the state lock when
    /// <paramref name="UnderStateLock"/>; <paramref name="Done"/> completes once it has run.
    /// </summary>
    public sealed record InOrder(Func<Task> Change, TaskCompletionSource Done, bool UnderStateLock = true) : PipelineItem;
}

/// <param name="ProcessId">The process that exited.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Stopped">
/// The server stopped it (Stop, delete, shutdown), whether it exited when interrupted or was killed
/// after the grace period; whoever stopped it decides the project's state.
/// </param>
/// <param name="Stderr">The last lines it wrote to stderr, oldest first; null if it wrote none.</param>
public sealed record ProcessExit(int ProcessId, int ExitCode, bool Stopped, string? Stderr);

/// <param name="At">When its exit was handled.</param>
/// <param name="Before">The project's status before the exit changed it.</param>
/// <param name="After">The status the exit left, the very instance: nothing has changed it since while it is still the project's.</param>
public sealed record ExitOnItsOwn(DateTime At, Shared.Models.ProjectStatus Before, Shared.Models.ProjectStatus After);
