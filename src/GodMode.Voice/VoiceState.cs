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
    private VoiceState _reported = VoiceState.Starting;

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
