using System.Threading.Channels;

namespace GodMode.Server.Models;

/// <summary>
/// A project's process state for as long as the server tracks the project: the pipeline its
/// output flows through, the locks that order what happens to it, and the process itself.
/// <see cref="Services.ProjectLifecycle"/> drives it; nothing else reads the pipeline.
/// </summary>
public sealed class ProjectProcess
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();

    /// <summary>
    /// Every line claude writes to stdout, plus the stderr errors surfaced to the UI, in the order
    /// they arrived. The process's pipe handlers only write here; the one consumer does the rest.
    /// </summary>
    public ChannelWriter<string> Output => _output.Writer;

    /// <summary>Serialises writes to claude's stdin, so two sends cannot mix their JSON lines.</summary>
    public SemaphoreSlim StdinLock { get; } = new(1, 1);

    /// <summary>
    /// Held by whoever changes <see cref="ProjectInfo.Status"/>: the consumer for state derived from
    /// output, and the project operations (send, stop, resume) for theirs.
    /// </summary>
    public SemaphoreSlim StateLock { get; } = new(1, 1);

    public int ProcessId { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>
    /// The most recent assistant text content block seen on the stream, used by
    /// the deterministic question detector to decide (on <c>result</c>) whether
    /// the turn ended with a question. Reset when a new turn starts.
    /// In-memory only; not persisted. Only the consumer touches it.
    /// </summary>
    public string? LastAssistantText { get; set; }

    private Task? _consumer;

    /// <summary>Starts the one consumer of <see cref="Output"/>, unless it is already running.</summary>
    public void EnsureConsumer(Func<ChannelReader<string>, Task> consume)
    {
        lock (_gate)
            _consumer ??= Task.Run(() => consume(_output.Reader));
    }

    /// <summary>Closes the pipeline and waits for the consumer to finish the lines already in it.</summary>
    public async Task CloseAsync()
    {
        _output.Writer.TryComplete();
        Task? consumer;
        lock (_gate) consumer = _consumer;
        if (consumer != null) await consumer;
    }
}
