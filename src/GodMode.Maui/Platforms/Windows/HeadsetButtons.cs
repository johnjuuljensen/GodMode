using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>A button Windows sent to GodMode's own media session.</summary>
public enum MediaButton
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
    Other,
}

/// <summary>
/// GodMode's own media session (Windows: its SystemMediaTransportControls): Windows sends a Bluetooth headset's buttons
/// only to the current session, never as keys (the headset spike, #382).
/// </summary>
public interface IOwnMediaSession
{
    /// <summary>Shows the session, playing or paused.</summary>
    void Open(bool playing);

    /// <summary>The status it reports, while it is open.</summary>
    void SetPlaying(bool playing);

    /// <summary>
    /// Closes the session and brings it back, playing: the way to be Windows' current session again once another one
    /// started playing (the spike's fifth trial: going paused and playing again does not do it).
    /// </summary>
    Task ReopenAsync();

    void Close();

    /// <summary>A button Windows sent it; on any thread.</summary>
    event Action<MediaButton>? Pressed;
}

/// <summary>The other media sessions (Windows: GSMTC), the music GodMode's session mirrors.</summary>
public interface IMediaSessions
{
    /// <summary>Whether any session but GodMode's plays; null while that cannot be told yet.</summary>
    bool? OthersPlaying { get; }

    /// <summary>Whether GodMode's own session is Windows' current one, the one the buttons go to.</summary>
    bool OwnIsCurrent { get; }

    /// <summary>Another session started playing: its id. On any thread.</summary>
    event Action<string>? OtherPlaying;

    /// <summary>Another session's status changed, or the sessions did. On any thread.</summary>
    event Action? OthersChanged;
}

/// <summary>
/// The headset's play/pause while voice is on (issue #423, the headset spike's listen mode): GodMode holds a media
/// session, so the headset's buttons come to it and not to Spotify, which the user runs by mouse. Play and Pause are
/// the mic's switch; Next, Previous and the rest are ignored. Holding the session takes two things the spike found:
/// <list type="bullet">
/// <item>its status mirrors the music's play/pause: a session saying "playing" with nothing playing got no button;</item>
/// <item>when another session starts playing, Windows makes it current, so GodMode's is checked
/// <see cref="ReclaimChecks">300 ms and 1 s later</see>, and closed and reopened while it is not current.</item>
/// </list>
/// </summary>
public sealed class HeadsetButtons : IDisposable
{
    /// <summary>When the session is checked after another one started playing, each wait after the one before.</summary>
    public static readonly IReadOnlyList<TimeSpan> ReclaimChecks = [TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];

    private readonly IOwnMediaSession _own;
    private readonly IMediaSessions _sessions;
    private readonly Func<Task> _playPause;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _reclaim;
    private bool _stopped;

    /// <param name="playPause">What Play and Pause do: the mic's toggle.</param>
    public HeadsetButtons(IOwnMediaSession own, IMediaSessions sessions, Func<Task> playPause, ILogger logger,
        TimeProvider? time = null)
    {
        _own = own;
        _sessions = sessions;
        _playPause = playPause;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _own.Pressed += Pressed;
        _sessions.OtherPlaying += OtherPlaying;
        _sessions.OthersChanged += Mirror;
        // Playing until the music is known, as the spike's listen mode started: a new session that plays is current
        _own.Open(playing: _sessions.OthersPlaying is not false);
        _logger.LogInformation("Voice: the headset's play/pause is the mic's switch");
    }

    /// <summary>Whether the session's buttons are still GodMode's: the reclaim stops with them.</summary>
    private bool Stopped
    {
        get
        {
            lock (_lock) return _stopped;
        }
    }

    private void Pressed(MediaButton button)
    {
        if (Stopped) return;
        if (button is MediaButton.Play or MediaButton.Pause)
        {
            _logger.LogInformation("Voice: the headset's {Button}: the mic's switch", button);
            _ = PlayPauseAsync();
        }
        else
            _logger.LogInformation("Voice: the headset's {Button} ignored", button);
    }

    private async Task PlayPauseAsync()
    {
        try
        {
            await _playPause().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the headset's play/pause failed");
        }
    }

    /// <summary>The status follows the other sessions' play/pause.</summary>
    private void Mirror()
    {
        if (Stopped || _sessions.OthersPlaying is not { } playing) return;
        _own.SetPlaying(playing);
    }

    private void OtherPlaying(string id)
    {
        CancellationToken ct;
        lock (_lock)
        {
            if (_stopped) return;
            _reclaim?.Cancel();
            _reclaim?.Dispose();
            _reclaim = new CancellationTokenSource();
            ct = _reclaim.Token;
        }
        Mirror();
        _ = ReclaimAsync(id, ct);
    }

    /// <summary>Another session started playing, and Windows makes it current: take it back, if Windows did.</summary>
    private async Task ReclaimAsync(string other, CancellationToken ct)
    {
        try
        {
            foreach (var wait in ReclaimChecks)
            {
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                if (Stopped) return;
                if (_sessions.OwnIsCurrent) return;
                _logger.LogInformation("Voice: {Other} played and is the current media session; GodMode's reopens", other);
                await _own.ReopenAsync().ConfigureAwait(false);
                Mirror();
            }
            if (!_sessions.OwnIsCurrent)
                _logger.LogWarning("Voice: {Other} is still the current media session: the headset's button goes to it", other);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: taking the media session back failed");
        }
    }

    /// <summary>The session closes, and the headset's buttons go to the music again.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            _reclaim?.Cancel();
            _reclaim?.Dispose();
            _reclaim = null;
        }
        _own.Pressed -= Pressed;
        _sessions.OtherPlaying -= OtherPlaying;
        _sessions.OthersChanged -= Mirror;
        _own.Close();
        _logger.LogInformation("Voice: the headset's buttons are the music's again");
    }
}
