using GodMode.Voice;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Media.Control;

namespace GodMode.Maui;

/// <summary>
/// Windows' media sessions for voice (issue #422), from the headset spike's <c>MediaSessions</c> and <c>Endpoints</c>
/// (#382): GSMTC (GlobalSystemMediaTransportControlsSessionManager) pauses and resumes what plays, Spotify among them,
/// and the render endpoints' volumes mark the audio route's switches. Windows keeps a volume per Bluetooth profile, so
/// with no hand on it a volume change is a switch: HFP's 0.2–0.5 s after the mic opens, A2DP's 5.2–5.3 s after it closes.
/// It is also the music GodMode's own media session mirrors (<see cref="IMediaSessions"/>, issue #423's headset buttons).
/// </summary>
/// <remarks>
/// Core Audio objects are made and used on background (MTA) threads: NAudio's fail across apartments, and the app's UI
/// thread is STA.
/// </remarks>
public sealed class WindowsMediaPlayback : IMediaPlayback, IMediaSessions, IMMNotificationClient
{
    /// <summary>How long a resume, or the manager it needs, is waited for.</summary>
    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly MediaSessionTracker<GlobalSystemMediaTransportControlsSession> _sessions;
    private readonly List<(MMDevice Device, AudioEndpointVolume Volume)> _volumes = [];
    private readonly Task<GlobalSystemMediaTransportControlsSessionManager?> _manager;
    private readonly MMDeviceEnumerator? _enumerator;
    private bool _micOpen;
    private bool _switched;
    private long _closedAt;
    private TaskCompletionSource _back = Completed();
    private bool _disposed;

    public WindowsMediaPlayback(ILogger logger)
    {
        _logger = logger;
        _sessions = new MediaSessionTracker<GlobalSystemMediaTransportControlsSession>(s => s.SourceAppUserModelId,
            s => StatusOf(s) is { } status ? status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : null,
            Watch, Unwatch, logger);
        _manager = StartSessionsAsync();
        try
        {
            _enumerator = Task.Run(() =>
            {
                var enumerator = new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(this);
                return enumerator;
            }).Result;
            Task.Run(WatchVolumes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: no audio endpoint notifications; the music resumes after the fallback once the mic closes");
        }
    }

    public event Action<string>? Playing
    {
        add => _sessions.Playing += value;
        remove => _sessions.Playing -= value;
    }

    public event Action<string>? OtherPlaying
    {
        add => _sessions.OtherPlaying += value;
        remove => _sessions.OtherPlaying -= value;
    }

    public event Action? OthersChanged
    {
        add => _sessions.OthersChanged += value;
        remove => _sessions.OthersChanged -= value;
    }

    /// <summary>The manager, once it is there; null before, or where Windows has none.</summary>
    private GlobalSystemMediaTransportControlsSessionManager? Manager =>
        _manager.IsCompletedSuccessfully ? _manager.Result : null;

    public bool? OthersPlaying => _sessions.OthersPlaying;

    public bool OwnIsCurrent => _sessions.OwnIsCurrent;

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    // ── GSMTC ──

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> StartSessionsAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_lock)
            {
                if (_disposed) return null;
                manager.SessionsChanged += SessionsChanged;
            }
            // Told once it is here: what changed before it, while the sessions could not be told, is mirrored now (issue #442)
            _sessions.Arrived(manager.GetSessions, manager.GetCurrentSession);
            return manager;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: Windows' media sessions are not available; the music is not paused");
            return null;
        }
    }

    /// <summary>
    /// Neither voice's own app nor another GodMode's is paused or played: a pause would come back as a button, and
    /// another GodMode's would toggle its mic.
    /// </summary>
    private bool IsMusic(GlobalSystemMediaTransportControlsSession session) => _sessions.IsMusic(session);

    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager manager, SessionsChangedEventArgs args)
    {
        try
        {
            _sessions.Listed();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: the media sessions cannot be listed");
        }
    }

    private void PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args) =>
        _sessions.StatusChanged(session);

    private void MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession session, MediaPropertiesChangedEventArgs args) =>
        ReadMarkerLater(session);

    private void Watch(GlobalSystemMediaTransportControlsSession session)
    {
        session.PlaybackInfoChanged += PlaybackInfoChanged;
        session.MediaPropertiesChanged += MediaPropertiesChanged;
        ReadMarkerLater(session);
    }

    private void Unwatch(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.PlaybackInfoChanged -= PlaybackInfoChanged;
            session.MediaPropertiesChanged -= MediaPropertiesChanged;
        }
        catch (Exception)
        {
            // A session that went away may throw as it is let go of
        }
    }

    /// <summary>Whose the session is, by the marker GodMode's own shows as its genre (<see cref="OwnMediaSession"/>).</summary>
    private void ReadMarkerLater(GlobalSystemMediaTransportControlsSession session) => Task.Run(async () =>
    {
        try
        {
            if (await Bounded(session.TryGetMediaPropertiesAsync().AsTask(), ResumeWait) is not { } properties) return;
            _sessions.Marked(session, properties.Genres?.FirstOrDefault(g => g.StartsWith(OwnMediaSession.MarkerPrefix, StringComparison.Ordinal)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: the media properties of {Session} cannot be read", session.SourceAppUserModelId);
        }
    });

    /// <summary>A WinRT call that may hang (a cold <c>RequestAsync</c>, an app that never answers), waited for at most <paramref name="wait"/>.</summary>
    private static async Task<T?> Bounded<T>(Task<T> call, TimeSpan wait)
    {
        try
        {
            return await call.WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }
        catch (TimeoutException)
        {
            return default;
        }
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? StatusOf(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> PausePlayingAsync(TimeSpan wait, CancellationToken ct)
    {
        var until = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        TimeSpan Left() => TimeSpan.FromMilliseconds(until - Environment.TickCount64);
        if (await Bounded(_manager, wait) is not { } manager) return [];
        var paused = new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var session in manager.GetSessions().Where(IsMusic))
        {
            if (StatusOf(session) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            try
            {
                // Asked, though not answered in time: it is still voice's to resume once it has paused
                var asked = session.TryPauseAsync().AsTask();
                if (await Bounded(asked, Left()) || !asked.IsCompleted) paused.Add(session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Voice: could not pause {Session}", session.SourceAppUserModelId);
            }
        }

        // The music stops once they say paused (Spotify 55–330 ms after the ask, in the spike)
        while (paused.Any(s => StatusOf(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               && Environment.TickCount64 < until)
            await Task.Delay(20, ct);
        return paused.Select(s => s.SourceAppUserModelId).ToArray();
    }

    public async Task ResumeAsync(IReadOnlyCollection<string> sessions, CancellationToken ct)
    {
        if (await Bounded(_manager, ResumeWait) is not { } manager) return;
        foreach (var session in manager.GetSessions().Where(s => sessions.Contains(s.SourceAppUserModelId) && IsMusic(s)))
        {
            if (StatusOf(session) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) continue;
            try
            {
                await Bounded(session.TryPlayAsync().AsTask(), ResumeWait);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Voice: could not resume {Session}", session.SourceAppUserModelId);
            }
        }
    }

    // ── The route: the render endpoints' volumes ──

    public void MicOpening()
    {
        lock (_lock)
        {
            _micOpen = true;
            _switched = false;
            _back.TrySetResult();
        }
    }

    public void MicClosed()
    {
        lock (_lock)
        {
            if (!_micOpen) return;
            _micOpen = false;
            _closedAt = Environment.TickCount64;
            // Opening it switched nothing (a laptop's microphone): nothing switches back either
            _back = _switched ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : Completed();
        }
    }

    public async Task FullQualityAsync(TimeSpan fallback, CancellationToken ct)
    {
        Task back;
        TimeSpan left;
        lock (_lock)
        {
            back = _back.Task;
            left = fallback - TimeSpan.FromMilliseconds(Environment.TickCount64 - _closedAt);
        }
        if (back.IsCompleted) return;
        try
        {
            await back.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, ct);
            _logger.LogInformation("Voice: the audio route is back, {Ms} ms after the mic closed", Environment.TickCount64 - _closedAt);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Voice: no switch back seen in {Fallback}; resuming all the same", fallback);
            // Back by now, as far as voice can tell: a later wait does not wait again
            lock (_lock)
            {
                if (!_micOpen) _back.TrySetResult();
            }
        }
    }

    private void RouteChanged()
    {
        lock (_lock)
        {
            if (_micOpen) _switched = true;
            else _back.TrySetResult();
        }
    }

    /// <summary>Watches each active render endpoint's volume afresh: on start, and whenever an endpoint comes or goes.</summary>
    private void WatchVolumes()
    {
        if (_enumerator is null) return;
        lock (_volumes)
        {
            if (_disposed) return;
            UnwatchVolumes();
            try
            {
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var volume = device.AudioEndpointVolume;
                        var last = volume.MasterVolumeLevelScalar;
                        // Windows notifies once per channel too: only a change of the master volume is a switch
                        volume.OnVolumeNotification += data =>
                        {
                            if (Math.Abs(Interlocked.Exchange(ref last, data.MasterVolume) - data.MasterVolume) < 0.0001f) return;
                            RouteChanged();
                        };
                        _volumes.Add((device, volume));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Voice: the volume of {Device} cannot be watched", device.ID);
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Voice: the render endpoints cannot be listed");
            }
        }
    }

    private void UnwatchVolumes()
    {
        foreach (var (device, volume) in _volumes)
        {
            try
            {
                volume.Dispose();
                device.Dispose();
            }
            catch (Exception)
            {
                // An endpoint that went away may throw as it is let go of
            }
        }
        _volumes.Clear();
    }

    // Off Core Audio's notification thread, which must not wait on the audio service
    private void WatchVolumesLater() => Task.Run(WatchVolumes);

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => WatchVolumesLater();
    public void OnDeviceAdded(string pwstrDeviceId) => WatchVolumesLater();
    public void OnDeviceRemoved(string deviceId) => WatchVolumesLater();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_volumes)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            UnwatchVolumes();
        }
        lock (_lock) _back.TrySetResult();
        _sessions.Dispose();
        if (Manager is { } manager) manager.SessionsChanged -= SessionsChanged;
        if (_enumerator is { } enumerator)
        {
            Task.Run(() =>
            {
                try
                {
                    enumerator.UnregisterEndpointNotificationCallback(this);
                    enumerator.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Voice: letting go of the endpoint notifications failed");
                }
            });
        }
    }
}
