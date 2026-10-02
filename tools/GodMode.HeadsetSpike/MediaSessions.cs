using System.Collections.Concurrent;
using Windows.Media.Control;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Windows' media sessions (GlobalSystemMediaTransportControlsSessionManager, GSMTC): every app that plays through the
/// media controls, Spotify among them. Logs sessions coming and going, the current one, and each one's playback state
/// and track; pauses and resumes them, the way step 2 of #382's flow would around an announcement.
/// </summary>
public sealed class MediaSessions(SpikeLog log)
{
    private const string Source = "GSMTC";
    private readonly ConcurrentDictionary<string, GlobalSystemMediaTransportControlsSession> _watched = new();
    private readonly ConcurrentDictionary<string, string> _states = new();
    private readonly ConcurrentBag<string> _pausedByUs = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <summary>A session's playback status changed (not only its position): its id and the new status; on a WinRT thread.</summary>
    public event Action<string, GlobalSystemMediaTransportControlsSessionPlaybackStatus>? StateChanged;

    /// <summary>Whether the session the proxy forwards to (the other one: playing, else the first) is playing; null for none.</summary>
    public bool? OtherPlaying()
    {
        var others = _manager?.GetSessions().Where(s => !IsOwn(s.SourceAppUserModelId)).ToList() ?? [];
        return others.Count == 0
            ? null
            : others.Any(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    public static bool IsOwn(string id) => id.Contains("GodMode.HeadsetSpike", StringComparison.OrdinalIgnoreCase);

    /// <summary>What plays, for the state line: the current session's app, state and track.</summary>
    public string Playing { get; private set; } = "(no session)";

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            log.Error(Source, "RequestAsync", ex);
            return;
        }
        _manager.SessionsChanged += (_, _) => Sessions();
        _manager.CurrentSessionChanged += (m, args) =>
        {
            log.Write(Source, $"current session: {m.GetCurrentSession()?.SourceAppUserModelId ?? "(none)"}");
            _ = UpdatePlayingAsync();
        };
        Sessions();
        log.Write(Source, $"current session: {_manager.GetCurrentSession()?.SourceAppUserModelId ?? "(none)"}");
    }

    public IReadOnlyList<string> SessionIds => _manager is null ? [] : [.. _manager.GetSessions().Select(s => s.SourceAppUserModelId)];

    private void Sessions()
    {
        if (_manager is null) return;
        var sessions = _manager.GetSessions();
        log.Write(Source, $"sessions: [{string.Join(", ", sessions.Select(s => s.SourceAppUserModelId))}]");
        foreach (var session in sessions)
        {
            var id = session.SourceAppUserModelId;
            if (!_watched.TryAdd(id, session)) continue;
            session.PlaybackInfoChanged += (s, _) => PlaybackState(s);
            session.MediaPropertiesChanged += (s, args) => _ = TrackAsync(s);
            PlaybackState(session);
            _ = TrackAsync(session);
        }
        foreach (var gone in _watched.Keys.Except(sessions.Select(s => s.SourceAppUserModelId)).ToList())
        {
            _watched.TryRemove(gone, out _);
            _states.TryRemove(gone, out _);
            log.Write(Source, $"{gone}: gone");
        }
        _ = UpdatePlayingAsync();
    }

    private void PlaybackState(GlobalSystemMediaTransportControlsSession session)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try
        {
            status = session.GetPlaybackInfo().PlaybackStatus;
        }
        catch (Exception ex)
        {
            log.Error(Source, "GetPlaybackInfo", ex);
            return;
        }
        var state = status.ToString();
        // PlaybackInfoChanged fires for more than the status (position, shuffle): only a change of status is logged
        if (_states.TryGetValue(session.SourceAppUserModelId, out var was) && was == state) return;
        _states[session.SourceAppUserModelId] = state;
        log.Write(Source, $"{session.SourceAppUserModelId}: {state}");
        StateChanged?.Invoke(session.SourceAppUserModelId, status);
        _ = UpdatePlayingAsync();
    }

    private async Task TrackAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            log.Write(Source, $"{session.SourceAppUserModelId}: track '{media.Artist} - {media.Title}'");
            await UpdatePlayingAsync();
        }
        catch (Exception ex)
        {
            log.Error(Source, "TryGetMediaPropertiesAsync", ex);
        }
    }

    private async Task UpdatePlayingAsync()
    {
        try
        {
            var current = _manager?.GetCurrentSession();
            if (current is null)
                Playing = "(no session)";
            else
            {
                var media = await current.TryGetMediaPropertiesAsync();
                Playing = $"{current.SourceAppUserModelId}: {current.GetPlaybackInfo().PlaybackStatus}, '{media.Artist} - {media.Title}'";
            }
        }
        catch (Exception ex)
        {
            Playing = $"(error: {ex.Message})";
        }
    }

    public Task PauseCurrentAsync() => OnCurrentAsync("TryPauseAsync", s => s.TryPauseAsync().AsTask());
    public Task PlayCurrentAsync() => OnCurrentAsync("TryPlayAsync", s => s.TryPlayAsync().AsTask());
    public Task ToggleCurrentAsync() => OnCurrentAsync("TryTogglePlayPauseAsync", s => s.TryTogglePlayPauseAsync().AsTask());

    private async Task OnCurrentAsync(string what, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act)
    {
        if (_manager?.GetCurrentSession() is not { } session)
        {
            log.Write(Source, $"{what}: no current session");
            return;
        }
        await RunAsync(session, what, act);
    }

    private async Task<bool> RunAsync(
        GlobalSystemMediaTransportControlsSession session, string what, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act)
    {
        log.Write(Source, $"{session.SourceAppUserModelId}: {what}");
        try
        {
            var done = await act(session);
            log.Write(Source, $"{session.SourceAppUserModelId}: {what} returned {done}");
            return done;
        }
        catch (Exception ex)
        {
            log.Error(Source, what, ex);
            return false;
        }
    }

    /// <summary>
    /// The proxy's half: a button the spike's own session got, passed on to the other session (the one playing, else
    /// the first): play and pause as a toggle, since the spike stays "playing" to stay the current session.
    /// </summary>
    public async Task ForwardAsync(Windows.Media.SystemMediaTransportControlsButton button)
    {
        var others = _manager?.GetSessions().Where(s => !IsOwn(s.SourceAppUserModelId)).ToList() ?? [];
        var target = others.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? others.FirstOrDefault();
        if (target is null)
        {
            log.Write(Source, $"forward {button}: no other session");
            return;
        }
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>>? act = button switch
        {
            Windows.Media.SystemMediaTransportControlsButton.Play or Windows.Media.SystemMediaTransportControlsButton.Pause =>
                s => s.TryTogglePlayPauseAsync().AsTask(),
            Windows.Media.SystemMediaTransportControlsButton.Next => s => s.TrySkipNextAsync().AsTask(),
            Windows.Media.SystemMediaTransportControlsButton.Previous => s => s.TrySkipPreviousAsync().AsTask(),
            Windows.Media.SystemMediaTransportControlsButton.Stop => s => s.TryStopAsync().AsTask(),
            _ => null,
        };
        if (act is null)
        {
            log.Write(Source, $"forward {button}: not forwarded");
            return;
        }
        await RunAsync(target, $"forwarded {button}", act);
    }

    /// <summary>Pauses every session that is playing, and remembers them for <see cref="ResumePausedAsync"/>: step 2's pause.</summary>
    public async Task<int> PausePlayingAsync()
    {
        if (_manager is null) return 0;
        var paused = 0;
        foreach (var session in _manager.GetSessions().Where(s => !IsOwn(s.SourceAppUserModelId))) // never its own: a pause would come back as a button
        {
            if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            if (await RunAsync(session, "TryPauseAsync (pause playing)", s => s.TryPauseAsync().AsTask()))
            {
                _pausedByUs.Add(session.SourceAppUserModelId);
                paused++;
            }
        }
        if (paused == 0) log.Write(Source, "pause playing: nothing was playing");
        return paused;
    }

    /// <summary>Resumes the sessions <see cref="PausePlayingAsync"/> paused, and only those.</summary>
    public async Task ResumePausedAsync()
    {
        if (_manager is null) return;
        var ids = new HashSet<string>();
        while (_pausedByUs.TryTake(out var id)) ids.Add(id);
        if (ids.Count == 0) log.Write(Source, "resume paused: none paused by the spike");
        foreach (var session in _manager.GetSessions().Where(s => ids.Contains(s.SourceAppUserModelId)))
            await RunAsync(session, "TryPlayAsync (resume paused)", s => s.TryPlayAsync().AsTask());
    }

    /// <summary>Waits until no session plays (or the timeout), for an announcement that starts once the music has stopped.</summary>
    public async Task<bool> WaitNonePlayingAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (_manager is null || _manager.GetSessions().Where(s => !IsOwn(s.SourceAppUserModelId)).All(s =>
                    s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
                return true;
            await Task.Delay(20);
        }
        return false;
    }
}
