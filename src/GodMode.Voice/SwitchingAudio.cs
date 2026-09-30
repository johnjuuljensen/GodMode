using System.Threading.Channels;
using VoiceBot.Core.Audio;

namespace GodMode.Voice;

/// <summary>
/// A microphone the session keeps while the device behind it changes: <see cref="Use"/> puts another source behind it,
/// and what the session reads goes on from there. VoiceBot's sources are opened on one device for good
/// (johnjuuljensen/VoiceBot#66), so following a default device, or a pinned one that goes, is
/// done here. A switch loses at most the audio in flight.
/// <para>
/// A source behind it that ends (its device went) does not end this one: it is reported to <c>ended</c>, which picks
/// another. Only <see cref="Complete"/> ends what the session reads.
/// </para>
/// </summary>
/// <param name="ended">The source in use ended by itself, with its error if it had one.</param>
public sealed class SwitchingAudioSource(AudioFormat format, Action<Exception?>? ended = null) : IAudioSource
{
    private readonly Channel<ReadOnlyMemory<byte>> _channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(1000);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _pump;
    private IAudioSource? _current;
    private int _generation;
    private bool _completed;

    public AudioFormat Format { get; } = format;
    public ChannelReader<ReadOnlyMemory<byte>> Audio => _channel.Reader;

    /// <summary>Takes the audio from <paramref name="source"/> from now on, and returns the one before for its owner to dispose.</summary>
    /// <exception cref="ArgumentException">It is in another format than the session reads.</exception>
    public IAudioSource? Use(IAudioSource source)
    {
        if (source.Format != Format)
            throw new ArgumentException($"The source gives {source.Format}; the session reads {Format}", nameof(source));

        CancellationTokenSource pump;
        IAudioSource? previous;
        int generation;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _pump?.Cancel();
            _pump?.Dispose();
            _pump = pump = new CancellationTokenSource();
            previous = _current;
            _current = source;
            generation = ++_generation;
        }
        _ = PumpAsync(source, generation, pump.Token);
        return previous;
    }

    /// <summary>Ends what the session reads, and returns the source in use for its owner to dispose.</summary>
    public IAudioSource? Complete()
    {
        IAudioSource? current;
        lock (_lock)
        {
            if (_completed) return null;
            _completed = true;
            _pump?.Cancel();
            _pump?.Dispose();
            _pump = null;
            current = _current;
            _current = null;
        }
        _channel.Writer.TryComplete();
        return current;
    }

    private bool IsCurrent(int generation)
    {
        lock (_lock) return generation == _generation && !_completed;
    }

    private async Task PumpAsync(IAudioSource source, int generation, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            await foreach (var chunk in source.Audio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // A chunk the source before had in hand as it was replaced is dropped, not put after the new one's
                if (!IsCurrent(generation)) return;
                _channel.Writer.TryWrite(chunk);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (IsCurrent(generation))
            ended?.Invoke(error);
    }
}

/// <summary>
/// The speaker the session keeps while the device behind it changes (see <see cref="SwitchingAudioSource"/>). With no
/// sink behind it, what the session plays is dropped.
/// </summary>
public sealed class SwitchingAudioSink(AudioFormat format) : IAudioSink
{
    private IAudioSink? _current;

    public AudioFormat Format { get; } = format;

    /// <summary>Plays on <paramref name="sink"/> from now on, and returns the one before for its owner to dispose.</summary>
    /// <exception cref="ArgumentException">It plays another format than the session sends.</exception>
    public IAudioSink? Use(IAudioSink? sink)
    {
        if (sink is not null && sink.Format != Format)
            throw new ArgumentException($"The sink plays {sink.Format}; the session sends {Format}", nameof(sink));
        return Interlocked.Exchange(ref _current, sink);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) =>
        Volatile.Read(ref _current)?.SendAudioAsync(audio, ct) ?? Task.CompletedTask;

    public Task SendStatusAsync(string message, CancellationToken ct) =>
        Volatile.Read(ref _current)?.SendStatusAsync(message, ct) ?? Task.CompletedTask;

    public Task InterruptAsync(CancellationToken ct) =>
        Volatile.Read(ref _current)?.InterruptAsync(ct) ?? Task.CompletedTask;
}
