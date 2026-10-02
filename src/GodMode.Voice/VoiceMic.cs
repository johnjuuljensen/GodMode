using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;

namespace GodMode.Voice;

/// <summary>Whether voice hears the user.</summary>
public enum VoiceMicState
{
    Closed,
    Open,
}

/// <summary>Why the mic closed, for the log.</summary>
public enum MicClose
{
    /// <summary>The Mic button.</summary>
    Button,

    /// <summary>A Done phrase (<see cref="DoneNode"/>).</summary>
    Done,

    /// <summary>Nothing was heard for the silence timeout.</summary>
    Silence,
}

/// <summary>The microphone that opens and closes on demand: <see cref="FollowingAudio"/>.</summary>
public interface IMicSwitch
{
    /// <summary>Opens it; blocks while the device opens.</summary>
    void OpenMic();

    void CloseMic();
}

/// <summary>How the mic behaves, with the defaults issue #422 names.</summary>
public sealed record VoiceMicOptions
{
    public const int DefaultSilenceSeconds = 10;

    /// <summary>Nothing heard while Listening for this long closes the mic.</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(DefaultSilenceSeconds);

    /// <summary>How long the rising and falling tones are.</summary>
    public TimeSpan ToneLength { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>The tones' loudness, of full scale.</summary>
    public float ToneVolume { get; init; } = 0.15f;

    /// <summary>
    /// How long the falling tone is given to play before the mic closes (the tone and the speaker's latency): it plays
    /// on the speaker the open mic uses, which closing it moves from.
    /// </summary>
    public TimeSpan ToneWait { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The mic, opened on demand (issue #422): closed when voice starts, so a Bluetooth headset stays in A2DP and nothing
/// goes to speech recognition. <see cref="OpenAsync"/> pauses the music (<see cref="MediaPause"/>), opens the
/// microphone (the switch to HFP), then plays the rising tone. It closes on <see cref="CloseAsync"/> (the button, a Done
/// phrase), or when nothing is heard while Listening for <see cref="VoiceMicOptions.SilenceTimeout"/>, counted from
/// the latest of the tone, the user's last words and the end of the bot's speech: the falling tone, then the microphone
/// is let go of, and the music resumes once the route is back at full quality.
/// </summary>
public sealed class VoiceMic : IDisposable
{
    private readonly IMicSwitch _mic;
    private readonly IAudioSink _speaker;
    private readonly MediaPause? _media;
    private readonly VoiceMicOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _lock = new();
    private VoiceMicState _state = VoiceMicState.Closed;
    private SessionActivity _activity = SessionActivity.Listening;
    private ITimer? _silence;
    private int _silenceRound;
    private bool _disposed;

    /// <param name="speaker">Where the tones play: the session's speaker, not through <see cref="MediaPause.Holding"/>.</param>
    /// <param name="media">The music to pause while the mic is open; null where the platform has none.</param>
    public VoiceMic(IMicSwitch mic, IAudioSink speaker, MediaPause? media, VoiceMicOptions options, ILogger logger,
        TimeProvider? time = null)
    {
        _mic = mic;
        _speaker = speaker;
        _media = media;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public VoiceMicState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    /// <summary>The mic opened or closed; on any thread.</summary>
    public event Action<VoiceMicState>? Changed;

    /// <summary>Pauses the music, opens the microphone, then plays the rising tone; the silence timer starts from it.</summary>
    public async Task OpenAsync()
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_disposed || _state == VoiceMicState.Open) return;
            }

            if (_media is not null)
            {
                try
                {
                    await _media.Hold(MediaHold.Mic).WaitAsync(MediaPause.PauseWait).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
            await Task.Run(_mic.OpenMic).ConfigureAwait(false);
            Set(VoiceMicState.Open);
            _logger.LogInformation("Voice: mic open");
            await ToneAsync(rising: true).ConfigureAwait(false);
            RestartSilence();
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Plays the falling tone, closes the microphone, and lets the music resume once the route is back.</summary>
    public async Task CloseAsync(MicClose why)
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_disposed || _state == VoiceMicState.Closed) return;
            }

            StopSilence();
            Set(VoiceMicState.Closed);
            _logger.LogInformation("Voice: mic closed ({Why})", why);
            await ToneAsync(rising: false).ConfigureAwait(false);
            if (_options.ToneWait > TimeSpan.Zero)
                await Task.Delay(_options.ToneWait, _time).ConfigureAwait(false);
            await Task.Run(_mic.CloseMic).ConfigureAwait(false);
            _media?.Release(MediaHold.Mic);
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>A Done phrase (<see cref="DoneNode"/>): the mic closes.</summary>
    public void Done() => _ = CloseAsync(MicClose.Done);

    /// <summary>The user said something (a partial or a final): the silence starts again from now.</summary>
    public void Heard() => RestartSilence();

    /// <summary>
    /// The session's activity: the silence counts only while it listens, from the moment it is back at Listening (the
    /// end of the bot's speech, or of its thinking).
    /// </summary>
    public void Activity(SessionActivity activity)
    {
        lock (_lock) _activity = activity;
        RestartSilence();
    }

    private void Set(VoiceMicState state)
    {
        lock (_lock) _state = state;
        Changed?.Invoke(state);
    }

    private async Task ToneAsync(bool rising)
    {
        try
        {
            await _speaker.SendAudioAsync(Tone(rising, _options.ToneLength, _options.ToneVolume, _speaker.Format), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the {Tone} tone did not play", rising ? "rising" : "falling");
        }
    }

    /// <summary>Starts the silence over, where it counts: the mic open, and the session listening.</summary>
    private void RestartSilence()
    {
        lock (_lock)
        {
            _silence?.Dispose();
            _silence = null;
            var round = ++_silenceRound;
            if (_disposed || _state != VoiceMicState.Open || _activity != SessionActivity.Listening) return;
            _silence = _time.CreateTimer(_ => Silent(round), null, _options.SilenceTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopSilence()
    {
        lock (_lock)
        {
            _silence?.Dispose();
            _silence = null;
            _silenceRound++;
        }
    }

    private void Silent(int round)
    {
        lock (_lock)
        {
            if (round != _silenceRound) return;
        }
        _ = CloseAsync(MicClose.Silence);
    }

    /// <summary>
    /// A sweep between 660 and 990 Hz, up for the open and down for the close, with 10 ms fades, in the speaker's PCM
    /// format (the headset spike's tone, #382).
    /// </summary>
    public static byte[] Tone(bool rising, TimeSpan length, float volume, AudioFormat format)
    {
        var frames = (int)(length.TotalSeconds * format.SampleRate);
        var fade = Math.Max(1, format.SampleRate / 100);
        var bytes = new byte[frames * format.Channels * 2];
        double phase = 0;
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / frames;
            var frequency = rising ? 660 + 330 * t : 990 - 330 * t;
            phase += 2 * Math.PI * frequency / format.SampleRate;
            var envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            var value = (short)(Math.Sin(phase) * envelope * volume * short.MaxValue);
            for (var c = 0; c < format.Channels; c++)
                BitConverter.TryWriteBytes(bytes.AsSpan((i * format.Channels + c) * 2), value);
        }
        return bytes;
    }

    /// <summary>Stops the silence timer. The microphone and the music are their owners' to let go of.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _silence?.Dispose();
            _silence = null;
        }
    }
}
