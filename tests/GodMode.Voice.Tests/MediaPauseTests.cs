using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Core.Pipeline;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #422: the music pauses while voice speaks or its mic is open, and resumes after, and only what voice paused
/// resumes. Over a fake of Windows' media sessions, so no Spotify.
/// </summary>
public sealed class MediaPauseTests : IDisposable
{
    private readonly FakePlayback _playback = new();
    private readonly MediaPause _media;

    /// <summary>Resuming at once after speech, so each test sees what it resumes; the delay has its own test.</summary>
    public MediaPauseTests() => _media = new MediaPause(_playback, NullLogger.Instance, speechResumeDelay: TimeSpan.Zero);

    public void Dispose() => _media.Dispose();

    [Fact]
    public async Task Speaking_pauses_what_plays_and_back_at_Listening_it_resumes()
    {
        _playback.Has("Spotify", playing: true);
        _playback.Has("Edge", playing: false);

        _media.Activity(SessionActivity.Speaking);
        await Eventually.UntilAsync(() => !_playback.IsPlaying("Spotify"), () => "Spotify still plays");
        Assert.Equal(["Spotify"], _media.Paused);

        _media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
        Assert.False(_playback.IsPlaying("Edge"));
        Assert.Equal(["pause Spotify", "resume Spotify"], _playback.Calls);
    }

    [Fact]
    public async Task Thinking_after_speaking_keeps_the_music_paused()
    {
        _playback.Has("Spotify", playing: true);

        _media.Activity(SessionActivity.Speaking);
        await Eventually.UntilAsync(() => !_playback.IsPlaying("Spotify"), () => "Spotify still plays");
        _media.Activity(SessionActivity.Thinking);
        await Task.Delay(50);

        Assert.False(_playback.IsPlaying("Spotify"));
        Assert.Equal(MediaHold.Speech, _media.Holds);
    }

    /// <summary>The session says Speaking as speech starts, but the first audio waits until the music has paused.</summary>
    [Fact]
    public async Task The_first_audio_waits_for_the_music_to_pause()
    {
        _playback.Has("Spotify", playing: true);
        _playback.PauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speaker = new RecordingAudioSink();
        var sink = _media.Holding(speaker);

        var sending = sink.SendAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(sending.IsCompleted);
        Assert.Empty(speaker.Calls);

        _playback.PauseGate.SetResult();
        await sending.WaitAsync(Eventually.Timeout);
        Assert.Single(speaker.Calls);
        await sink.SendAudioAsync(new byte[] { 3 }, CancellationToken.None).WaitAsync(Eventually.Timeout);
        Assert.Equal(["pause Spotify"], _playback.Calls);
    }

    [Fact]
    public async Task The_first_audio_waits_no_longer_than_400_ms()
    {
        _playback.Has("Spotify", playing: true);
        _playback.PauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speaker = new RecordingAudioSink();
        var clock = Stopwatch.StartNew();

        await _media.Holding(speaker).SendAudioAsync(new byte[] { 1 }, CancellationToken.None).WaitAsync(Eventually.Timeout);

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(3));
        Assert.Single(speaker.Calls);
        _playback.PauseGate.SetResult();
    }

    /// <summary>
    /// Review of PR#425: only the first audio waits for the pause, and a pause that never answers holds speech for
    /// 400 ms once, never the music: it resumes all the same once the pause comes in late.
    /// </summary>
    [Fact]
    public async Task A_pause_that_hangs_holds_only_the_first_audio_and_the_music_still_resumes_after()
    {
        _playback.Has("Spotify", playing: true);
        _playback.PauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speaker = new RecordingAudioSink();
        var sink = _media.Holding(speaker);
        _media.Activity(SessionActivity.Speaking);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
            await sink.SendAudioAsync(new byte[] { (byte)i }, CancellationToken.None).WaitAsync(Eventually.Timeout);
        Assert.Equal(5, speaker.Calls.Count);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(750));

        _media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _media.Holds == MediaHold.None, () => "still held");
        _playback.PauseGate.SetResult();
        await Eventually.UntilAsync(() => _playback.Calls.Contains("resume Spotify"), () => $"calls: {string.Join(", ", _playback.Calls)}");
        Assert.True(_playback.IsPlaying("Spotify"));
    }

    /// <summary>
    /// Review of PR#425: GSMTC tells of voice's own resume late, perhaps after the next announcement paused the music
    /// again. That is no play by hand: the music is still voice's to resume.
    /// </summary>
    [Fact]
    public async Task Its_own_resume_told_late_is_not_a_play_by_hand()
    {
        _playback.Has("Spotify", playing: true);
        _playback.HoldPlaying = true;
        _media.Activity(SessionActivity.Speaking);
        _media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _playback.Calls.Contains("resume Spotify"), () => "Spotify was not resumed");

        _media.Activity(SessionActivity.Speaking);
        await Eventually.UntilAsync(() => _playback.Calls.Count(c => c == "pause Spotify") == 2, () => "Spotify was not paused again");
        _playback.TellPlaying();
        Assert.Equal(["Spotify"], _media.Paused);

        _media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify stayed paused");
    }

    /// <summary>Review of PR#425: announcements back to back, the next within a second, keep the music paused between them.</summary>
    [Fact]
    public async Task Announcements_back_to_back_keep_the_music_paused_between_them()
    {
        using var media = new MediaPause(_playback, NullLogger.Instance);
        _playback.Has("Spotify", playing: true);

        media.Activity(SessionActivity.Speaking);
        media.Activity(SessionActivity.Listening);
        await Task.Delay(300);
        media.Activity(SessionActivity.Speaking);
        await Task.Delay(1200);
        Assert.Equal(["pause Spotify"], _playback.Calls);

        media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
    }

    /// <summary>Spotify paused by hand before an announcement is not voice's to resume.</summary>
    [Fact]
    public async Task Music_paused_before_is_not_resumed()
    {
        _playback.Has("Spotify", playing: false);

        _media.Activity(SessionActivity.Speaking);
        _media.Activity(SessionActivity.Listening);
        await Task.Delay(100);

        Assert.False(_playback.IsPlaying("Spotify"));
        Assert.DoesNotContain(_playback.Calls, c => c.StartsWith("resume", StringComparison.Ordinal));
    }

    /// <summary>
    /// The user took the music back during an announcement (played it by hand, then paused it): it is theirs, and
    /// voice does not resume it.
    /// </summary>
    [Fact]
    public async Task Music_played_by_hand_while_paused_is_the_users_again_and_is_not_resumed()
    {
        _playback.Has("Spotify", playing: true);
        _media.Activity(SessionActivity.Speaking);
        await Eventually.UntilAsync(() => _media.Paused.Count == 1, () => "Spotify was not paused");

        _playback.PlayByHand("Spotify");
        _playback.PauseByHand("Spotify");
        _media.Activity(SessionActivity.Listening);
        await Task.Delay(100);

        Assert.False(_playback.IsPlaying("Spotify"));
        Assert.Equal(["pause Spotify"], _playback.Calls);
    }

    /// <summary>The mic open holds the music; closed, it resumes once the headset is back at full quality (A2DP).</summary>
    [Fact]
    public async Task The_mic_holds_the_music_until_the_route_is_back_at_full_quality()
    {
        _playback.Has("Spotify", playing: true);
        _playback.FullQuality = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _media.Hold(MediaHold.Mic).WaitAsync(Eventually.Timeout);
        _media.Activity(SessionActivity.Speaking);
        _media.Activity(SessionActivity.Listening);
        await Task.Delay(50);
        Assert.False(_playback.IsPlaying("Spotify"));

        _media.Release(MediaHold.Mic);
        await Task.Delay(50);
        Assert.False(_playback.IsPlaying("Spotify"));

        _playback.FullQuality.SetResult();
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
        Assert.Equal(["mic opening", "pause Spotify", "mic closed", "resume Spotify"], _playback.Calls);
    }

    /// <summary>An announcement while the music waits to resume after the mic keeps it paused until the announcement is over.</summary>
    [Fact]
    public async Task A_hold_while_the_music_waits_to_resume_calls_the_resume_off()
    {
        _playback.Has("Spotify", playing: true);
        _playback.FullQuality = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _media.Hold(MediaHold.Mic).WaitAsync(Eventually.Timeout);
        _media.Release(MediaHold.Mic);

        _media.Activity(SessionActivity.Speaking);
        _playback.FullQuality.SetResult();
        await Task.Delay(100);
        Assert.False(_playback.IsPlaying("Spotify"));

        _media.Activity(SessionActivity.Listening);
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
    }

    [Fact]
    public async Task Voice_stopping_gives_the_music_back_and_lets_go_of_the_playback()
    {
        _playback.Has("Spotify", playing: true);
        await _media.Hold(MediaHold.Mic).WaitAsync(Eventually.Timeout);

        _media.Dispose();

        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify") && _playback.Disposed, () => "Spotify was not resumed, or the playback kept");
        Assert.Contains("mic closed", _playback.Calls);
    }
}

/// <summary>Windows' media sessions, as voice sees them: each session's id and whether it plays, and what voice asked of them.</summary>
internal sealed class FakePlayback : IMediaPlayback
{
    private readonly ConcurrentDictionary<string, bool> _sessions = new();
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => [.. _calls];
    public bool Disposed { get; private set; }

    /// <summary>Pauses wait for it, where set: the music takes its time to stop.</summary>
    public TaskCompletionSource? PauseGate { get; set; }

    /// <summary>When the route is back at full quality after the mic closed; at once unless set.</summary>
    public TaskCompletionSource? FullQuality { get; set; }

    /// <summary>Where calls go too, for a test that checks their order against others'.</summary>
    public ConcurrentQueue<string>? Log { get; set; }

    public event Action<string>? Playing;

    public void Has(string session, bool playing) => _sessions[session] = playing;
    public bool IsPlaying(string session) => _sessions.GetValueOrDefault(session);

    public void PlayByHand(string session)
    {
        _sessions[session] = true;
        Playing?.Invoke(session);
    }

    public void PauseByHand(string session) => _sessions[session] = false;

    private void Call(string call)
    {
        _calls.Enqueue(call);
        Log?.Enqueue(call);
    }

    public async Task<IReadOnlyList<string>> PausePlayingAsync(TimeSpan wait, CancellationToken ct)
    {
        if (PauseGate is { } gate) await gate.Task;
        var paused = _sessions.Where(s => s.Value).Select(s => s.Key).Order().ToList();
        foreach (var session in paused)
        {
            _sessions[session] = false;
            Call($"pause {session}");
        }
        return paused;
    }

    /// <summary>Its resumes' Playing is held until <see cref="TellPlaying"/>, as GSMTC tells it late.</summary>
    public bool HoldPlaying { get; set; }

    private readonly ConcurrentQueue<string> _heldPlaying = new();

    public void TellPlaying()
    {
        while (_heldPlaying.TryDequeue(out var session)) Playing?.Invoke(session);
    }

    public Task ResumeAsync(IReadOnlyCollection<string> sessions, CancellationToken ct)
    {
        foreach (var session in sessions.Where(s => _sessions.TryGetValue(s, out var playing) && !playing))
        {
            _sessions[session] = true;
            Call($"resume {session}");
            if (HoldPlaying) _heldPlaying.Enqueue(session);
            else Playing?.Invoke(session);
        }
        return Task.CompletedTask;
    }

    public void MicOpening() => Call("mic opening");
    public void MicClosed() => Call("mic closed");

    public Task FullQualityAsync(TimeSpan fallback, CancellationToken ct) =>
        FullQuality is { } back ? back.Task.WaitAsync(ct) : Task.CompletedTask;

    public void Dispose() => Disposed = true;
}
