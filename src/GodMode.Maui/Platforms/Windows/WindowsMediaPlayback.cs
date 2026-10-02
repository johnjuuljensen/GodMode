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
/// </summary>
/// <remarks>
/// Core Audio objects are made and used on background (MTA) threads: NAudio's fail across apartments, and the app's UI
/// thread is STA.
/// </remarks>
public sealed class WindowsMediaPlayback : IMediaPlayback, IMMNotificationClient
{
    /// <summary>How long a resume, or the manager it needs, is waited for.</summary>
    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSessionPlaybackStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watched = new(StringComparer.Ordinal);
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

    public event Action<string>? Playing;

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
            WatchSessions(manager);
            return manager;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: Windows' media sessions are not available; the music is not paused");
            return null;
        }
    }

    /// <summary>Voice's own app is never paused: a pause would come back as a button.</summary>
    private static bool IsOwn(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId.Contains("GodMode", StringComparison.OrdinalIgnoreCase);

    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager manager, SessionsChangedEventArgs args) =>
        WatchSessions(manager);

    private void PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args) =>
        StatusChanged(session);

    private void WatchSessions(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        foreach (var session in manager.GetSessions())
        {
            lock (_lock)
            {
                if (_disposed || !_watched.TryAdd(session.SourceAppUserModelId, session)) continue;
                session.PlaybackInfoChanged += PlaybackInfoChanged;
            }
            StatusChanged(session);
        }
    }

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

    /// <summary>PlaybackInfoChanged fires for more than the status (position, shuffle): only a change to playing is told.</summary>
    private void StatusChanged(GlobalSystemMediaTransportControlsSession session)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try
        {
            status = session.GetPlaybackInfo().PlaybackStatus;
        }
        catch (Exception)
        {
            return;
        }
        var id = session.SourceAppUserModelId;
        lock (_lock)
        {
            if (_statuses.TryGetValue(id, out var was) && was == status) return;
            _statuses[id] = status;
        }
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) Playing?.Invoke(id);
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
        foreach (var session in manager.GetSessions().Where(s => !IsOwn(s)))
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
        return [.. paused.Select(s => s.SourceAppUserModelId)];
    }

    public async Task ResumeAsync(IReadOnlyCollection<string> sessions, CancellationToken ct)
    {
        if (await Bounded(_manager, ResumeWait) is not { } manager) return;
        foreach (var session in manager.GetSessions().Where(s => sessions.Contains(s.SourceAppUserModelId)))
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
        GlobalSystemMediaTransportControlsSession[] sessions;
        lock (_lock)
        {
            _back.TrySetResult();
            sessions = [.. _watched.Values];
            _watched.Clear();
            foreach (var session in sessions) session.PlaybackInfoChanged -= PlaybackInfoChanged;
        }
        if (_manager.IsCompletedSuccessfully && _manager.Result is { } manager) manager.SessionsChanged -= SessionsChanged;
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
