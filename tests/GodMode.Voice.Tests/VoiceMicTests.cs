using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #422's mic: it opens on demand (the music paused, then the microphone, then the rising tone), and closes on
/// the button, on a Done phrase (on a final only), or after the silence timeout while voice listens. Over fakes, on a
/// clock the test moves.
/// </summary>
public sealed class VoiceMicTests : IDisposable
{
    private static readonly VoiceMicOptions Options = new() { ToneWait = TimeSpan.Zero };

    private readonly ConcurrentQueue<string> _log = new();
    private readonly ManualTime _time = new();
    private readonly FakePlayback _playback = new();
    private readonly MediaPause _media;
    private readonly ToneSink _speaker;
    private readonly FakeMicSwitch _switch;
    private readonly VoiceMic _mic;

    public VoiceMicTests()
    {
        _playback.Log = _log;
        _media = new MediaPause(_playback, NullLogger.Instance);
        _speaker = new ToneSink(_log);
        _switch = new FakeMicSwitch(_log);
        _mic = new VoiceMic(_switch, _speaker, _media, Options, NullLogger.Instance, _time);
    }

    public void Dispose()
    {
        _mic.Dispose();
        _media.Dispose();
    }

    [Fact]
    public async Task Voice_starts_with_the_mic_closed_and_opening_it_pauses_the_music_then_opens_it_then_plays_the_rising_tone()
    {
        _playback.Has("Spotify", playing: true);
        var changes = new ConcurrentQueue<VoiceMicState>();
        _mic.Changed += changes.Enqueue;
        Assert.Equal(VoiceMicState.Closed, _mic.State);

        await _mic.OpenAsync();

        Assert.Equal(VoiceMicState.Open, _mic.State);
        Assert.Equal(["mic opening", "pause Spotify", "open", "rising tone"], _log);
        Assert.Equal([VoiceMicState.Open], changes);
    }

    [Fact]
    public async Task Closing_plays_the_falling_tone_then_lets_go_of_the_microphone_and_the_music_resumes_once_the_route_is_back()
    {
        _playback.Has("Spotify", playing: true);
        _playback.FullQuality = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _mic.OpenAsync();

        await _mic.CloseAsync(MicClose.Button);
        Assert.Equal(VoiceMicState.Closed, _mic.State);
        Assert.False(_playback.IsPlaying("Spotify"));

        _playback.FullQuality.SetResult();
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
        Assert.Equal(["mic opening", "pause Spotify", "open", "rising tone", "falling tone", "close", "mic closed", "resume Spotify"], _log);
    }

    [Fact]
    public async Task Ten_seconds_of_silence_while_listening_close_the_mic()
    {
        await _mic.OpenAsync();

        _time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(VoiceMicState.Open, _mic.State);
        _time.Advance(TimeSpan.FromSeconds(0.2));

        await Eventually.UntilAsync(() => _switch.Closes == 1, () => "the mic did not close");
        Assert.Equal(VoiceMicState.Closed, _mic.State);
        Assert.Contains("falling tone", _log);
    }

    [Fact]
    public async Task The_users_words_start_the_silence_over()
    {
        await _mic.OpenAsync();

        _time.Advance(TimeSpan.FromSeconds(8));
        _mic.Heard();
        _time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(VoiceMicState.Open, _mic.State);

        _time.Advance(TimeSpan.FromSeconds(2.1));
        await Eventually.UntilAsync(() => _mic.State == VoiceMicState.Closed, () => "the mic did not close");
    }

    /// <summary>The silence counts only while voice listens: from the end of its speech, however long it thought and spoke.</summary>
    [Fact]
    public async Task The_silence_counts_from_the_end_of_the_bots_speech()
    {
        await _mic.OpenAsync();

        _mic.Activity(SessionActivity.Thinking);
        _time.Advance(TimeSpan.FromSeconds(30));
        _mic.Activity(SessionActivity.Speaking);
        _time.Advance(TimeSpan.FromSeconds(30));
        _mic.Activity(SessionActivity.Listening);
        _time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(VoiceMicState.Open, _mic.State);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        await Eventually.UntilAsync(() => _mic.State == VoiceMicState.Closed, () => "the mic did not close");
    }

    [Fact]
    public async Task A_closed_mic_has_no_silence_timer_and_opening_it_twice_opens_it_once()
    {
        _time.Advance(TimeSpan.FromSeconds(60));
        await _mic.OpenAsync();
        await _mic.OpenAsync();
        await _mic.CloseAsync(MicClose.Button);
        await _mic.CloseAsync(MicClose.Button);
        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal((1, 1), (_switch.Opens, _switch.Closes));
    }

    /// <summary>
    /// Review of PR#425: the Mic pressed during an announcement, to answer it. Opening moves the speaker, and the one it
    /// leaves drops what it holds, so the mic opens once the speech has played out; the music is paused at once.
    /// </summary>
    [Fact]
    public async Task The_mic_pressed_while_the_bot_speaks_opens_once_the_speech_has_played_out()
    {
        _playback.Has("Spotify", playing: true);
        _mic.Activity(SessionActivity.Speaking);

        var opening = _mic.OpenAsync();
        await Eventually.UntilAsync(() => !_playback.IsPlaying("Spotify"), () => "Spotify still plays");
        await Task.Delay(100);
        Assert.Equal((0, VoiceMicState.Closed), (_switch.Opens, _mic.State));

        _mic.Activity(SessionActivity.Listening);
        await opening.WaitAsync(Eventually.Timeout);
        Assert.Equal((1, VoiceMicState.Open), (_switch.Opens, _mic.State));
    }

    [Fact]
    public async Task The_mic_closed_while_the_bot_speaks_lets_go_once_the_speech_has_played_out()
    {
        await _mic.OpenAsync();
        _mic.Activity(SessionActivity.Speaking);

        var closing = _mic.CloseAsync(MicClose.Button);
        await Task.Delay(100);
        Assert.Equal(0, _switch.Closes);
        Assert.DoesNotContain("falling tone", _log);

        _mic.Activity(SessionActivity.Listening);
        await closing.WaitAsync(Eventually.Timeout);
        Assert.Equal(1, _switch.Closes);
    }

    /// <summary>A mic that does not open leaves it closed, says why, and lets the music go.</summary>
    [Fact]
    public async Task A_mic_that_does_not_open_stays_closed_and_the_music_resumes()
    {
        _playback.Has("Spotify", playing: true);
        _switch.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(_mic.OpenAsync);

        Assert.Equal(VoiceMicState.Closed, _mic.State);
        await Eventually.UntilAsync(() => _playback.IsPlaying("Spotify"), () => "Spotify was not resumed");
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, _switch.Closes);
    }

    [Fact]
    public void The_tones_sweep_up_and_down_in_the_speakers_format()
    {
        var rising = VoiceMic.Tone(rising: true, TimeSpan.FromMilliseconds(80), 0.15f, AudioFormat.Pcm16kHz);
        var falling = VoiceMic.Tone(rising: false, TimeSpan.FromMilliseconds(80), 0.15f, AudioFormat.Pcm16kHz);

        Assert.Equal(16_000 * 80 / 1000 * 2, rising.Length);
        Assert.NotEqual(rising, falling);
        var peak = Enumerable.Range(0, rising.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(rising, i * 2)));
        Assert.InRange(peak, 0.1 * short.MaxValue, 0.16 * short.MaxValue);
        Assert.Equal(0, BitConverter.ToInt16(rising, 0));
    }

    // ── DoneNode, through a session ──

    [Theory]
    [InlineData("Færdig.")]
    [InlineData("Færdig tak.")]
    [InlineData("Det var alt.")]
    [InlineData("Done.")]
    [InlineData("That's all.")]
    [InlineData("Færdig. Færdig.")]
    public async Task A_Done_phrase_closes_the_mic_and_the_model_is_not_called(string said)
    {
        var model = new ScriptedChatClient();
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, mic: _mic);
        await voice.Events.SaidAsync("Klar.");
        await _mic.OpenAsync();

        voice.Transcriptions.SayAsRecognized(said);

        await Eventually.UntilAsync(() => _mic.State == VoiceMicState.Closed, () => "the mic did not close");
        Assert.Equal(0, model.Calls);
    }

    /// <summary>"Færdig" may begin a sentence: a partial never closes the mic, and a final that says more is the model's.</summary>
    [Fact]
    public async Task Done_is_heard_on_a_final_only_and_a_sentence_that_begins_with_it_is_the_models()
    {
        var model = new ScriptedChatClient().Respond("Ukendt.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, mic: _mic);
        await voice.Events.SaidAsync("Klar.");
        await _mic.OpenAsync();

        voice.Transcriptions.AddPartial("Færdig");
        voice.Transcriptions.AddPartial("Færdig med");
        voice.Transcriptions.AddFinal("Færdig med 283?");
        await voice.Events.SaidAsync("Ukendt.");

        Assert.Equal(VoiceMicState.Open, _mic.State);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void The_Done_phrases_are_keyterms()
    {
        Assert.All(["færdig", "det var alt", "done", "that's all"], term => Assert.Contains(term, GodModeGraph.CommandWords));
        Assert.True(DoneNode.Said("Færdig tak!"));
        Assert.False(DoneNode.Said("Færdig med 283"));
    }

    private sealed class FakeMicSwitch(ConcurrentQueue<string> log) : IMicSwitch
    {
        private int _opens;
        private int _closes;

        public int Opens => Volatile.Read(ref _opens);
        public int Closes => Volatile.Read(ref _closes);

        public bool Fail { get; set; }

        public void OpenMic()
        {
            if (Fail) throw new InvalidOperationException("No microphone opened");
            Interlocked.Increment(ref _opens);
            log.Enqueue("open");
        }

        public void CloseMic()
        {
            Interlocked.Increment(ref _closes);
            log.Enqueue("close");
        }
    }

    /// <summary>The speaker, telling the tones apart.</summary>
    private sealed class ToneSink(ConcurrentQueue<string> log) : IAudioSink
    {
        private static readonly byte[] Rising = VoiceMic.Tone(true, Options.ToneLength, Options.ToneVolume, AudioFormat.Pcm16kHz);
        private static readonly byte[] Falling = VoiceMic.Tone(false, Options.ToneLength, Options.ToneVolume, AudioFormat.Pcm16kHz);

        public AudioFormat Format => AudioFormat.Pcm16kHz;

        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            log.Enqueue(audio.Span.SequenceEqual(Rising) ? "rising tone" : audio.Span.SequenceEqual(Falling) ? "falling tone" : "audio");
            return Task.CompletedTask;
        }

        public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
