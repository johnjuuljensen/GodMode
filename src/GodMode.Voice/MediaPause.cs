using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;

namespace GodMode.Voice;

/// <summary>
/// The platform's media sessions, the music voice pauses (Windows: GSMTC, Spotify among them), and the audio route the
/// music comes back on (a Bluetooth headset's A2DP).
/// </summary>
public interface IMediaPlayback : IDisposable
{
    /// <summary>
    /// Pauses every media session that plays, never voice's own, and returns the ones it paused once they say they have,
    /// or once <paramref name="wait"/> is up.
    /// </summary>
    Task<IReadOnlyList<string>> PausePlayingAsync(TimeSpan wait, CancellationToken ct);

    /// <summary>Plays these sessions again: those of them still there and paused.</summary>
    Task ResumeAsync(IReadOnlyCollection<string> sessions, CancellationToken ct);

    /// <summary>A media session started playing, by anyone: its id. On any thread.</summary>
    event Action<string>? Playing;

    /// <summary>The microphone is about to open: what changes in the audio route from now is the switch (to HFP).</summary>
    void MicOpening();

    /// <summary>The microphone has closed: the route goes back by itself (to A2DP, about 5.2 s later on the OpenRun Pro 2).</summary>
    void MicClosed();

    /// <summary>
    /// Returns once the route is back at full quality: at once when opening the mic switched nothing, else when the
    /// switch back shows, at the latest <paramref name="fallback"/> after the mic closed.
    /// </summary>
    Task FullQualityAsync(TimeSpan fallback, CancellationToken ct);
}

/// <summary>What holds the music paused.</summary>
[Flags]
public enum MediaHold
{
    None = 0,

    /// <summary>The session speaks (<see cref="SessionActivity.Speaking"/>, until it is back at Listening).</summary>
    Speech = 1,

    /// <summary>The mic is open.</summary>
    Mic = 2,
}

/// <summary>
/// Pauses the music while voice speaks or its mic is open, and resumes it after: the first hold
/// (<see cref="Hold"/>) pauses what plays, and the last release (<see cref="Release"/>) resumes it, once the route is
/// back at full quality (<see cref="IMediaPlayback.FullQualityAsync"/>). It resumes only what it paused, and of that
/// only what nobody played meanwhile: a session started by hand is the user's again, and so is whatever they do with it
/// after (pause it again, say). A session playing because of its own resume is not one played by hand.
/// <para>
/// After speech alone (the mic never opened), it waits <see cref="DefaultSpeechResumeDelay"/> before it resumes: the
/// next announcement in the queue comes within it, and the music does not flap between them. Every call to the platform
/// is bounded, so a pause that hangs neither stalls the speech nor keeps the music from resuming.
/// </para>
/// </summary>
public sealed class MediaPause : IDisposable
{
    /// <summary>How long voice waits for the music to pause before it speaks, or opens the mic.</summary>
    public static readonly TimeSpan PauseWait = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after the mic closed the music resumes if the route's switch back never shows.</summary>
    public static readonly TimeSpan DefaultFullQualityFallback = TimeSpan.FromSeconds(8);

    /// <summary>How long after speech, with the mic closed throughout, the music waits to resume.</summary>
    public static readonly TimeSpan DefaultSpeechResumeDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long a resume is waited for before it is let go of.</summary>
    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(2);

    /// <summary>How long after its own resume a session's playing is put down to it, not to the user.</summary>
    private static readonly TimeSpan OwnResumeWindow = TimeSpan.FromSeconds(3);

    private readonly IMediaPlayback _playback;
    private readonly ILogger _logger;
    private readonly TimeSpan _fallback;
    private readonly TimeSpan _speechResumeDelay;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _resumedAt = new(StringComparer.Ordinal);
    private MediaHold _holds;
    private bool _micHeld;
    private Task _pausing = Task.CompletedTask;
    private CancellationTokenSource? _resuming;
    private bool _disposed;

    public MediaPause(IMediaPlayback playback, ILogger logger, TimeSpan? fullQualityFallback = null,
        TimeSpan? speechResumeDelay = null, TimeProvider? time = null)
    {
        _playback = playback;
        _logger = logger;
        _fallback = fullQualityFallback ?? DefaultFullQualityFallback;
        _speechResumeDelay = speechResumeDelay ?? DefaultSpeechResumeDelay;
        _time = time ?? TimeProvider.System;
        _playback.Playing += Played;
    }

    public MediaHold Holds
    {
        get
        {
            lock (_lock) return _holds;
        }
    }

    /// <summary>The sessions it paused and will resume, for tests and the log.</summary>
    public IReadOnlyList<string> Paused
    {
        get
        {
            lock (_lock) return [.. _paused];
        }
    }

    /// <summary>
    /// Holds the music paused for <paramref name="reason"/>. The first hold pauses what plays, and calls off a resume
    /// still waiting. Returns the pause, done once the music has paused (at most <see cref="PauseWait"/>).
    /// </summary>
    public Task Hold(MediaHold reason)
    {
        lock (_lock)
        {
            if (_disposed || (_holds & reason) == reason) return _pausing;
            var was = _holds;
            _holds |= reason;
            if (reason.HasFlag(MediaHold.Mic) && !was.HasFlag(MediaHold.Mic))
            {
                _micHeld = true;
                _playback.MicOpening();
            }
            if (was != MediaHold.None) return _pausing;

            CancelResume();
            return _pausing = PauseAsync();
        }
    }

    /// <summary>Lets go of <paramref name="reason"/>'s hold; the last one resumes what it paused.</summary>
    public void Release(MediaHold reason)
    {
        lock (_lock)
        {
            if (_disposed || (_holds & reason) == MediaHold.None) return;
            if ((_holds & reason).HasFlag(MediaHold.Mic)) _playback.MicClosed();
            _holds &= ~reason;
            if (_holds != MediaHold.None) return;

            // After the mic, the route takes its time to come back anyway; after speech alone, the next may be queued
            var delay = _micHeld ? TimeSpan.Zero : _speechResumeDelay;
            _micHeld = false;
            StartResume(delay);
        }
    }

    private void StartResume(TimeSpan delay)
    {
        CancelResume();
        _resuming = new CancellationTokenSource();
        _ = ResumeAsync(delay, _resuming.Token);
    }

    /// <summary>The session's activity: speaking holds the music, back at Listening lets it go.</summary>
    public void Activity(SessionActivity activity)
    {
        switch (activity)
        {
            case SessionActivity.Speaking:
                _ = Hold(MediaHold.Speech);
                break;
            case SessionActivity.Listening:
                Release(MediaHold.Speech);
                break;
        }
    }

    /// <summary>
    /// The speaker, holding its first audio until the music has paused (at most <see cref="PauseWait"/>): the session's
    /// activity says Speaking as speech starts, but its audio may come first.
    /// </summary>
    public IAudioSink Holding(IAudioSink speaker) => new HoldingSink(this, speaker);

    /// <summary>Pauses what plays, done within <see cref="PauseWait"/> however long the platform takes.</summary>
    private async Task PauseAsync()
    {
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            var call = _playback.PausePlayingAsync(PauseWait, CancellationToken.None);
            try
            {
                Remember(await call.WaitAsync(PauseWait).ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Voice: the music did not pause within {Wait}; voice goes on", PauseWait);
                _ = PausedLateAsync(call);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: could not pause the music");
        }
        finally
        {
            _calls.Release();
        }
    }

    private void Remember(IReadOnlyList<string> paused)
    {
        lock (_lock) _paused.UnionWith(paused);
        if (paused.Count > 0)
            _logger.LogInformation("Voice: paused {Sessions}", string.Join(", ", paused));
    }

    /// <summary>A pause that came after voice stopped waiting: what it paused is resumed too, at once if nothing holds it.</summary>
    private async Task PausedLateAsync(Task<IReadOnlyList<string>> call)
    {
        try
        {
            Remember(await call.ConfigureAwait(false));
            lock (_lock)
            {
                if (!_disposed && _holds == MediaHold.None) StartResume(TimeSpan.Zero);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: could not pause the music");
        }
    }

    private async Task ResumeAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            Task pausing;
            lock (_lock) pausing = _pausing;
            await pausing.ConfigureAwait(false);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            await _playback.FullQualityAsync(_fallback, ct).ConfigureAwait(false);

            await _calls.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string[] sessions;
                lock (_lock)
                {
                    if (ct.IsCancellationRequested) return;
                    sessions = [.. _paused];
                    _paused.Clear();
                    var now = _time.GetTimestamp();
                    foreach (var session in sessions) _resumedAt[session] = now;
                }
                if (sessions.Length == 0) return;
                await _playback.ResumeAsync(sessions, CancellationToken.None).WaitAsync(ResumeWait).ConfigureAwait(false);
                _logger.LogInformation("Voice: resumed {Sessions}", string.Join(", ", sessions));
            }
            finally
            {
                _calls.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: could not resume the music");
        }
    }

    private void CancelResume()
    {
        _resuming?.Cancel();
        _resuming?.Dispose();
        _resuming = null;
    }

    /// <summary>
    /// A session played. Soon after its own resume of it, that is the resume (GSMTC tells it late, perhaps after the next
    /// pause); otherwise, for a session it paused, the user played it, and it is theirs now.
    /// </summary>
    private void Played(string session)
    {
        bool forgotten;
        lock (_lock)
        {
            if (_resumedAt.Remove(session, out var at) && _time.GetElapsedTime(at) < OwnResumeWindow) return;
            forgotten = _paused.Remove(session);
        }
        if (forgotten)
            _logger.LogInformation("Voice: {Session} was played by hand; it is not resumed", session);
    }

    /// <summary>
    /// Lets go of every hold: what it paused resumes as it would, and the playback is disposed after. Voice stopping
    /// with the mic open gives the music back.
    /// </summary>
    public void Dispose()
    {
        Task resuming;
        lock (_lock)
        {
            if (_disposed) return;
            if (_holds.HasFlag(MediaHold.Mic)) _playback.MicClosed();
            _holds = MediaHold.None;
            _disposed = true;
            CancelResume();
            resuming = ResumeAsync(TimeSpan.Zero, CancellationToken.None);
        }
        _ = resuming.ContinueWith(_ =>
        {
            _playback.Playing -= Played;
            _playback.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>Holds only the first audio after a pause began: the rest goes straight on, however the pause is doing.</summary>
    private sealed class HoldingSink(MediaPause media, IAudioSink speaker) : IAudioSink
    {
        private Task? _waited;

        public AudioFormat Format => speaker.Format;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            var pausing = media.Hold(MediaHold.Speech);
            if (!ReferenceEquals(Interlocked.Exchange(ref _waited, pausing), pausing) && !pausing.IsCompleted)
            {
                try
                {
                    await pausing.WaitAsync(PauseWait, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
            await speaker.SendAudioAsync(audio, ct).ConfigureAwait(false);
        }

        public Task SendStatusAsync(string message, CancellationToken ct) => speaker.SendStatusAsync(message, ct);
        public Task InterruptAsync(CancellationToken ct) => speaker.InterruptAsync(ct);
    }
}
