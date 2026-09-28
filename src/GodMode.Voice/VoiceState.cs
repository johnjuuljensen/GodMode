using Microsoft.Extensions.AI;
using VoiceBot.Core.AI;
using VoiceBot.Core.Audio;

namespace GodMode.Voice;

/// <summary>What the voice button shows.</summary>
public enum VoiceState
{
    Off,
    Starting,
    Listening,
    Thinking,
    Speaking,
    /// <summary>It cannot go on as it is: it failed to start, stopped on a failure, or a service refused its key.</summary>
    Error,
}

/// <summary>
/// Works out <see cref="VoiceState"/> from what the session does, since VoiceBot reports no such state: speaking
/// while the audio sent to the sink plays, thinking while the model works (and a moment after, across its tool calls),
/// listening otherwise. Raises <see cref="Changed"/> when it changes.
/// </summary>
public sealed class VoiceStateTracker : IAsyncDisposable
{
    /// <summary>How long it stays thinking after the model answers, so a tool call between rounds is not a flicker.</summary>
    public static readonly TimeSpan ThinkingLinger = TimeSpan.FromMilliseconds(600);

    private readonly Lock _lock = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(100));
    private readonly Task _loop;
    private int _inference;
    private long _thinkingUntil;
    private long _speakingUntil;
    private VoiceState _held = VoiceState.Starting;
    private VoiceState _reported = VoiceState.Starting;

    public VoiceStateTracker() => _loop = TickAsync();

    public event Action<VoiceState>? Changed;

    public VoiceState Current
    {
        get { lock (_lock) return Compute(); }
    }

    /// <summary>
    /// A state that holds whatever the session does, until <see cref="Release"/>: <see cref="VoiceState.Error"/> for a
    /// refused key, or <see cref="VoiceState.Off"/> once it stopped.
    /// </summary>
    public void Hold(VoiceState state)
    {
        lock (_lock) _held = state;
        Report();
    }

    /// <summary>The session runs normally (it started, or a refused service works again).</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_held != VoiceState.Off) _held = VoiceState.Listening;
        }
        Report();
    }

    /// <summary>The user finished saying something: the model gets it next.</summary>
    public void UserSpoke()
    {
        lock (_lock) _thinkingUntil = Math.Max(_thinkingUntil, Environment.TickCount64 + (long)ThinkingLinger.TotalMilliseconds);
        Report();
    }

    public void InferenceStarted()
    {
        lock (_lock) _inference++;
        Report();
    }

    public void InferenceEnded()
    {
        lock (_lock)
        {
            _inference--;
            _thinkingUntil = Environment.TickCount64 + (long)ThinkingLinger.TotalMilliseconds;
        }
        Report();
    }

    /// <summary>Audio of this length went to the sink: it plays after what is queued there already.</summary>
    public void AudioSent(TimeSpan duration)
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            _speakingUntil = Math.Max(_speakingUntil, now) + (long)duration.TotalMilliseconds;
        }
        Report();
    }

    /// <summary>The sink dropped what it had queued (barge-in).</summary>
    public void Interrupted()
    {
        lock (_lock) _speakingUntil = 0;
        Report();
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await _loop;
    }

    private VoiceState Compute()
    {
        if (_held is VoiceState.Off or VoiceState.Error or VoiceState.Starting)
            return _held;
        var now = Environment.TickCount64;
        return now < _speakingUntil ? VoiceState.Speaking
            : _inference > 0 || now < _thinkingUntil ? VoiceState.Thinking
            : VoiceState.Listening;
    }

    private void Report()
    {
        VoiceState state;
        lock (_lock)
        {
            state = Compute();
            if (state == _reported) return;
            _reported = state;
        }
        Changed?.Invoke(state);
    }

    private async Task TickAsync()
    {
        while (await _timer.WaitForNextTickAsync())
            Report();
    }
}

/// <summary>An audio sink that tells the <see cref="VoiceStateTracker"/> how long what it was sent plays.</summary>
public sealed class ObservedAudioSink(IAudioSink inner, VoiceStateTracker state) : IAudioSink
{
    public AudioFormat Format => inner.Format;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        await inner.SendAudioAsync(audio, ct);
        state.AudioSent(TimeSpan.FromSeconds((double)audio.Length / Format.BytesPerSecond));
    }

    public Task SendStatusAsync(string message, CancellationToken ct) => inner.SendStatusAsync(message, ct);

    public async Task InterruptAsync(CancellationToken ct)
    {
        await inner.InterruptAsync(ct);
        state.Interrupted();
    }
}

/// <summary>An inference provider that tells the <see cref="VoiceStateTracker"/> while the model works.</summary>
public sealed class ObservedInference(IInferenceProvider inner, VoiceStateTracker state) : IInferenceProvider
{
    public IChatClient GetClient(InferenceTier tier) => inner.GetClient(tier);

    public async Task<ChatResponse> CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        state.InferenceStarted();
        try
        {
            return await inner.CompleteAsync(tier, messages, options, ct);
        }
        finally
        {
            state.InferenceEnded();
        }
    }
}
