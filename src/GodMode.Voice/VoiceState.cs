using VoiceBot.Core.Pipeline;

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
/// Works out <see cref="VoiceState"/>: the session's <see cref="SessionActivity"/> while it runs normally, unless a state
/// is held over it (<see cref="Hold"/>). Raises <see cref="Changed"/> when it changes.
/// </summary>
public sealed class VoiceStateTracker
{
    private readonly Lock _lock = new();
    private SessionActivity _activity = SessionActivity.Listening;
    private VoiceState _held = VoiceState.Starting;
    private SessionService? _heldFor;
    private VoiceState _reported = VoiceState.Starting;

    public event Action<VoiceState>? Changed;

    public VoiceState Current
    {
        get { lock (_lock) return Compute(); }
    }

    /// <summary>
    /// A state that holds whatever the session does, until <see cref="Release"/>: <see cref="VoiceState.Error"/> for a
    /// refused key or a failed microphone, or <see cref="VoiceState.Off"/> once it stopped.
    /// </summary>
    /// <param name="failed">The service whose failure it is: only its own recovery releases it.</param>
    public void Hold(VoiceState state, SessionService? failed = null)
    {
        lock (_lock)
        {
            _held = state;
            _heldFor = failed;
        }
        Report();
    }

    /// <summary>
    /// The session runs normally: it started (<paramref name="recovered"/> null), or <paramref name="recovered"/> works
    /// again. An error held for another service stays: an STT reconnect is no working microphone.
    /// </summary>
    public void Release(SessionService? recovered = null)
    {
        lock (_lock)
        {
            if (_held == VoiceState.Off || (recovered is not null && _heldFor is { } failed && failed != recovered)) return;
            _held = VoiceState.Listening;
            _heldFor = null;
        }
        Report();
    }

    /// <summary>The session reported what it is doing (<see cref="ISessionEventSink.OnActivityAsync"/>).</summary>
    public void Activity(SessionActivity activity)
    {
        lock (_lock) _activity = activity;
        Report();
    }

    private VoiceState Compute() =>
        _held is VoiceState.Off or VoiceState.Error or VoiceState.Starting ? _held
        : _activity switch
        {
            SessionActivity.Thinking => VoiceState.Thinking,
            SessionActivity.Speaking => VoiceState.Speaking,
            _ => VoiceState.Listening,
        };

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
}
