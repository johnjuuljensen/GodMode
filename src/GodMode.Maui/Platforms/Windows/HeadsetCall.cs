using GodMode.Voice;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>A call the platform made, not yet active (Windows: a <c>VoipPhoneCall</c>).</summary>
public interface IPhoneCall
{
    /// <summary>Reports it active. Windows may raise <see cref="EndRequested"/> before this returns.</summary>
    void NotifyActive();

    void NotifyEnded();

    /// <summary>The headset's button asked to end it; on any thread.</summary>
    event Action? EndRequested;
}

/// <summary>Where the headset's calls come from (Windows: the VoIP call coordinator and its call control devices).</summary>
public interface IPhoneLine
{
    /// <summary>A new call, with the headset as its call control device; null where none can be made (it says why).</summary>
    Task<IPhoneCall?> RequestAsync();
}

/// <summary>
/// The headset's call (issue #423) over its platform's <see cref="IPhoneLine"/>: one at a time, an <see cref="End"/>
/// during a start ends the call that start makes, and the headset's button ends it at once (Windows wants it ended
/// within 5 s), then is told. Compiled into the voice tests from its source, as <see cref="HeadsetButtons"/> is.
/// </summary>
public sealed class HeadsetCall(IPhoneLine line, ILogger logger) : IHeadsetCall
{
    private readonly Lock _lock = new();
    private IPhoneCall? _call;
    // Each End moves it on, so a start still under way ends the call it makes
    private int _round;
    private bool _disposed;

    public event Action? EndRequested;

    public async Task StartAsync()
    {
        int round;
        lock (_lock)
        {
            if (_disposed || _call is not null) return;
            round = _round;
        }
        if (await line.RequestAsync().ConfigureAwait(false) is not { } call) return;
        call.EndRequested += () => Ended(call);
        // The call is the current one before it is active: the headset's button may end it while it becomes so (issue #442)
        bool current;
        lock (_lock)
        {
            current = !_disposed && round == _round;
            if (current) _call = call;
        }
        if (!current)
        {
            // Ended while it started
            NotifyEnded(call);
            return;
        }
        try
        {
            call.NotifyActive();
        }
        catch
        {
            lock (_lock)
            {
                if (_call == call) _call = null;
            }
            throw;
        }
        logger.LogInformation("Voice: the headset's call is active");
    }

    /// <summary>The headset's button: the call ends now, and the mic closes after.</summary>
    private void Ended(IPhoneCall call)
    {
        lock (_lock)
        {
            if (_call != call) return;
            _call = null;
        }
        NotifyEnded(call);
        logger.LogInformation("Voice: the headset's button ended the call");
        EndRequested?.Invoke();
    }

    public void End()
    {
        IPhoneCall? call;
        lock (_lock)
        {
            _round++;
            call = _call;
            _call = null;
        }
        if (call is not null) NotifyEnded(call);
    }

    private void NotifyEnded(IPhoneCall call)
    {
        try
        {
            call.NotifyEnded();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice: the headset's call did not end cleanly");
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        End();
    }
}
