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
/// after (pause it again, say).
/// </summary>
public sealed class MediaPause : IDisposable
{
    /// <summary>How long voice waits for the music to pause before it speaks, or opens the mic.</summary>
    public static readonly TimeSpan PauseWait = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after the mic closed the music resumes if the route's switch back never shows.</summary>
    public static readonly TimeSpan DefaultFullQualityFallback = TimeSpan.FromSeconds(8);

    private readonly IMediaPlayback _playback;
    private readonly ILogger _logger;
    private readonly TimeSpan _fallback;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private MediaHold _holds;
    private Task _pausing = Task.CompletedTask;
    private CancellationTokenSource? _resuming;
    private bool _disposed;

    public MediaPause(IMediaPlayback playback, ILogger logger, TimeSpan? fullQualityFallback = null)
    {
        _playback = playback;
        _logger = logger;
        _fallback = fullQualityFallback ?? DefaultFullQualityFallback;
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
            if (reason.HasFlag(MediaHold.Mic) && !was.HasFlag(MediaHold.Mic)) _playback.MicOpening();
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

            CancelResume();
            _resuming = new CancellationTokenSource();
            _ = ResumeAsync(_resuming.Token);
        }
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

    private async Task PauseAsync()
    {
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            var paused = await _playback.PausePlayingAsync(PauseWait, CancellationToken.None).ConfigureAwait(false);
            lock (_lock) _paused.UnionWith(paused);
            if (paused.Count > 0)
                _logger.LogInformation("Voice: paused {Sessions}", string.Join(", ", paused));
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

    private async Task ResumeAsync(CancellationToken ct)
    {
        try
        {
            Task pausing;
            lock (_lock) pausing = _pausing;
            await pausing.ConfigureAwait(false);
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
                }
                if (sessions.Length == 0) return;
                await _playback.ResumeAsync(sessions, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>A session it paused played again, and not by its resume: it is the user's now.</summary>
    private void Played(string session)
    {
        bool forgotten;
        lock (_lock) forgotten = _paused.Remove(session);
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
            resuming = ResumeAsync(CancellationToken.None);
        }
        _ = resuming.ContinueWith(_ =>
        {
            _playback.Playing -= Played;
            _playback.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed class HoldingSink(MediaPause media, IAudioSink speaker) : IAudioSink
    {
        public AudioFormat Format => speaker.Format;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            var pausing = media.Hold(MediaHold.Speech);
            if (!pausing.IsCompleted)
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
