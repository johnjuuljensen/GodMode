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

    /// <summary>The headset's button: its play/pause, or the end of its call (<see cref="IHeadsetCall.EndRequested"/>).</summary>
    Headset,
}

/// <summary>The microphone that opens and closes on demand: <see cref="FollowingAudio"/>.</summary>
public interface IMicSwitch
{
    /// <summary>Opens it; blocks while the device opens.</summary>
    void OpenMic();

    void CloseMic();
}

/// <summary>
/// A call the platform shows while the mic is open, whose call control device is the headset (issue #423: Windows 11
/// 24H2's VoIP calls): in HFP the headset's button is no media button, and reaches the app only as the call's end.
/// </summary>
public interface IHeadsetCall : IDisposable
{
    /// <summary>Reports the call active, with the headset as its call control device.</summary>
    Task StartAsync();

    /// <summary>Ends the call, if there is one.</summary>
    void End();

    /// <summary>The headset's button asked to end the call, which has ended; on any thread.</summary>
    event Action? EndRequested;
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
/// is let go of, and the music resumes once the route is back at full quality. Pressed while the bot speaks, either
/// waits for its speech to play out: the speaker moves with the mic, and the one it leaves drops what it holds.
/// </summary>
public sealed class VoiceMic : IDisposable
{
    private readonly IMicSwitch _mic;
    private readonly IAudioSink _speaker;
    private readonly MediaPause? _media;
    private readonly IHeadsetCall? _call;
    private readonly VoiceMicOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _lock = new();
    private VoiceMicState _state = VoiceMicState.Closed;
    private SessionActivity _activity = SessionActivity.Listening;
    private TaskCompletionSource _quiet = Quiet();
    private ITimer? _silence;
    private int _silenceRound;
    private bool _disposed;

    /// <param name="speaker">Where the tones play: the session's speaker, not through <see cref="MediaPause.Holding"/>.</param>
    /// <param name="media">The music to pause while the mic is open; null where the platform has none.</param>
    /// <param name="call">The call held while the mic is open, whose end closes it; null where the platform has none.</param>
    public VoiceMic(IMicSwitch mic, IAudioSink speaker, MediaPause? media, VoiceMicOptions options, ILogger logger,
        TimeProvider? time = null, IHeadsetCall? call = null)
    {
        _mic = mic;
        _speaker = speaker;
        _media = media;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _call = call;
        if (_call is not null) _call.EndRequested += HeadsetEnded;
    }

    private static TaskCompletionSource Quiet()
    {
        var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        quiet.SetResult();
        return quiet;
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

    /// <summary>
    /// Pauses the music, opens the microphone, then plays the rising tone, and holds the headset's call; the silence
    /// timer starts from it.
    /// </summary>
    public Task OpenAsync() => SwitchAsync(OpenHeldAsync);

    /// <summary>Ends the headset's call, plays the falling tone, closes the microphone, and lets the music resume once the route is back.</summary>
    public Task CloseAsync(MicClose why) => SwitchAsync(() => CloseHeldAsync(why));

    /// <summary>
    /// The headset's play/pause (issue #423): opens the mic when it is closed, closes it when it is open. A press while
    /// the mic opens or closes waits for that switch, then switches it back: no press is dropped.
    /// </summary>
    public Task ToggleAsync() => SwitchAsync(() => State == VoiceMicState.Open ? CloseHeldAsync(MicClose.Headset) : OpenHeldAsync());

    /// <summary>One switch at a time: what each does is decided once the one before it is done.</summary>
    private async Task SwitchAsync(Func<Task> switchHeld)
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            await switchHeld().ConfigureAwait(false);
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task OpenHeldAsync()
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
        await QuietAsync().ConfigureAwait(false);
        lock (_lock)
        {
            if (_disposed) return;
        }
        try
        {
            await Task.Run(_mic.OpenMic).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the mic did not open");
            _media?.Release(MediaHold.Mic);
            throw;
        }
        Set(VoiceMicState.Open);
        _logger.LogInformation("Voice: mic open");
        await ToneAsync(rising: true).ConfigureAwait(false);
        await StartCallAsync().ConfigureAwait(false);
        RestartSilence();
    }

    private async Task CloseHeldAsync(MicClose why)
    {
        lock (_lock)
        {
            if (_disposed || _state == VoiceMicState.Closed) return;
        }

        StopSilence();
        Set(VoiceMicState.Closed);
        _logger.LogInformation("Voice: mic closed ({Why})", why);
        _call?.End();
        await QuietAsync().ConfigureAwait(false);
        await ToneAsync(rising: false).ConfigureAwait(false);
        if (_options.ToneWait > TimeSpan.Zero)
            await Task.Delay(_options.ToneWait, _time).ConfigureAwait(false);
        await Task.Run(_mic.CloseMic).ConfigureAwait(false);
        _media?.Release(MediaHold.Mic);
    }

    /// <summary>
    /// The headset's call, held while the mic is open: in HFP its button reaches GodMode only as the call's end. A call
    /// that does not start leaves the mic open, to close by silence, a Done phrase or the app's button.
    /// </summary>
    private async Task StartCallAsync()
    {
        if (_call is null) return;
        try
        {
            await _call.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the headset's call did not start; its button cannot close the mic");
        }
    }

    /// <summary>The headset's button in the call: the call has ended, and the mic closes.</summary>
    private void HeadsetEnded() => _ = CloseAsync(MicClose.Headset);

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
        lock (_lock)
        {
            _activity = activity;
            if (activity == SessionActivity.Speaking && _quiet.Task.IsCompleted)
                _quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else if (activity != SessionActivity.Speaking)
                _quiet.TrySetResult();
        }
        RestartSilence();
    }

    /// <summary>
    /// Returns once the session is not speaking: opening or closing the mic moves Default for the speaker between the
    /// communications and console devices, and the speaker it leaves drops what it still holds, so speech plays out first.
    /// </summary>
    private Task QuietAsync()
    {
        lock (_lock) return _quiet.Task;
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

    /// <summary>
    /// Stops the silence timer and ends the headset's call. The microphone and the music are their owners' to let go of.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _silence?.Dispose();
            _silence = null;
            _quiet.TrySetResult();
        }
        if (_call is not null)
        {
            _call.EndRequested -= HeadsetEnded;
            _call.End();
        }
    }
}
