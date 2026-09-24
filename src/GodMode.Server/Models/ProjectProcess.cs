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
/// <param name="Killed">The server killed it (Stop, shutdown, a relaunch), which then decides the project's state.</param>
/// <param name="Stderr">The last lines it wrote to stderr, oldest first; null if it wrote none.</param>
public sealed record ProcessExit(int ProcessId, int ExitCode, bool Killed, string? Stderr);
