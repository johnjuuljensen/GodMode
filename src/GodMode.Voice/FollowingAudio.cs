using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;

namespace GodMode.Voice;

/// <summary>A microphone a platform opened on one device, not yet capturing.</summary>
public interface IMicrophone : IAudioSource, IDisposable
{
    /// <summary>What captures, for the log.</summary>
    string Description { get; }

    void Start();
}

/// <summary>A speaker a platform opened on one device.</summary>
public interface ISpeaker : IAudioSink, IDisposable;

/// <summary>A platform's microphones and speakers, for <see cref="FollowingAudio"/>.</summary>
public interface IAudioDevices
{
    /// <summary>
    /// The devices there are, and the defaults voice follows: the communications defaults, or with
    /// <paramref name="consoleDefaults"/> the console (multimedia) ones. Those are what the platform's echo canceller
    /// opens (on Windows the Voice Capture DSP pairs them), and the speaker voice plays on while its mic is closed, so a
    /// Bluetooth headset stays in A2DP.
    /// </summary>
    VoiceDeviceList List(bool consoleDefaults);

    /// <summary>Calls <paramref name="changed"/> when a device comes or goes or a default changes, until disposed.</summary>
    IDisposable Watch(Action changed);

    /// <param name="echoCancelled">Through the platform's echo canceller, which opens the defaults itself.</param>
    IMicrophone OpenMicrophone(AudioDevice device, bool echoCancelled);

    ISpeaker OpenSpeaker(AudioDevice device);
}

/// <summary>
/// The microphone and speaker a session keeps while the devices behind them change: each is the device the settings
/// chose while it is there, else the default, and is picked again whenever the devices change (a Bluetooth headset
/// turned on or off) or the settings choose others. What the session holds are a <see cref="SwitchingAudioSource"/> and
/// <see cref="SwitchingAudioSink"/>; the platform's are opened anew behind them on the device picked.
/// <para>
/// Echo cancellation runs only with Default for both devices: the platform's canceller opens the default pair itself.
/// With a chosen device voice captures without it, and says so in the log.
/// </para>
/// <para>
/// The microphone opens on demand (<see cref="OpenMic"/>, <see cref="CloseMic"/>): while it is closed, the session reads
/// a silent source, no microphone is open (on a Bluetooth headset, none holds it in HFP), and Default for the speaker is
/// the console (multimedia) default, A2DP's. While it is open, Default is the communications pair. A chosen speaker is
/// the one chosen, either way.
/// </para>
/// </summary>
public sealed class FollowingAudio : IMicSwitch, IDisposable
{
    /// <summary>A headset turning on raises several notifications in a row: the devices are picked once they settle.</summary>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(500);

    private readonly IAudioDevices _devices;
    private readonly ILogger _logger;
    private readonly bool _echoCancellation;
    private readonly TimeSpan _settleTime;
    private readonly SwitchingAudioSource _source;
    private readonly SwitchingAudioSink _sink = new(AudioFormat.Pcm16kHz);
    private readonly Lock _picking = new();
    private readonly Timer _settle;
    private readonly IDisposable? _watch;
    private AudioDevice? _microphone;
    private AudioDevice? _speaker;
    private Opened? _openedMicrophone;
    private Opened? _openedSpeaker;
    private IMicrophone? _current;
    private bool _micOpen;
    private bool _started;
    private bool _disposed;

    /// <summary>What is open, to tell whether a new pick changes anything.</summary>
    private sealed record Opened(string? Id, bool EchoCancelled);

    /// <summary>The microphone is closed: the session reads a silent source.</summary>
    private static readonly Opened Closed = new("(closed)", false);

    /// <summary>Opens the devices the settings mean now (the microphone not capturing until <see cref="Start"/>) and watches for changes.</summary>
    /// <param name="microphone">The chosen microphone; null for Default.</param>
    /// <param name="speaker">The chosen speaker; null for Default.</param>
    /// <param name="micOpen">Whether the microphone opens now, or waits for <see cref="OpenMic"/>.</param>
    public FollowingAudio(IAudioDevices devices, bool echoCancellation, AudioDevice? microphone, AudioDevice? speaker,
        ILogger logger, TimeSpan? settle = null, bool micOpen = true)
    {
        _micOpen = micOpen;
        _devices = devices;
        _logger = logger;
        _echoCancellation = echoCancellation;
        _microphone = microphone;
        _speaker = speaker;
        _settleTime = settle ?? DefaultSettle;
        _source = new SwitchingAudioSource(AudioFormat.Pcm16kHz, MicrophoneEnded);
        _settle = new Timer(_ => Pick("the devices changed"));
        try
        {
            Pick("voice started");
            _watch = devices.Watch(PickSoon);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IAudioSource Source => _source;
    public IAudioSink Sink => _sink;

    /// <summary>The device each is open on now (null for none, and for the mic closed), for tests and the log.</summary>
    public (string? Microphone, string? Speaker) OpenIds
    {
        get
        {
            lock (_picking) return (_openedMicrophone == Closed ? null : _openedMicrophone?.Id, _openedSpeaker?.Id);
        }
    }

    /// <summary>Whether the microphone is open: the session hears it, rather than silence.</summary>
    public bool MicOpen
    {
        get
        {
            lock (_picking) return _micOpen;
        }
    }

    /// <summary>
    /// Opens the microphone the settings mean, and moves Default for the speaker to the communications default. Blocks
    /// while the device opens: on a Bluetooth headset, the switch to HFP.
    /// </summary>
    public void OpenMic() => SwitchMic(true);

    /// <summary>
    /// Closes the microphone, so the session reads silence, and moves Default for the speaker back to the console
    /// default. Letting go of the device lets a Bluetooth headset go back to A2DP.
    /// </summary>
    public void CloseMic() => SwitchMic(false);

    private void SwitchMic(bool open)
    {
        lock (_picking)
        {
            if (_micOpen == open) return;
            _micOpen = open;
        }
        Pick(open ? "the mic opened" : "the mic closed");
    }

    public void Start()
    {
        lock (_picking)
        {
            _started = true;
            _current?.Start();
        }
    }

    /// <summary>The settings chose other devices (null for Default): voice moves to them now.</summary>
    public void UseDevices(AudioDevice? microphone, AudioDevice? speaker)
    {
        lock (_picking)
        {
            _microphone = microphone;
            _speaker = speaker;
        }
        Pick("the settings chose other devices");
    }

    /// <summary>The microphone in use stopped by itself (it went away): it is opened again, or another picked.</summary>
    private void MicrophoneEnded(Exception? error)
    {
        _logger.LogWarning(error, "Voice: the microphone stopped; picking one again");
        lock (_picking)
        {
            if (_openedMicrophone == Closed) return;
            _openedMicrophone = null;
        }
        PickSoon();
    }

    /// <summary>Picks once the devices settle; a notification that comes as voice stops is let go.</summary>
    private void PickSoon()
    {
        try
        {
            _settle.Change(_settleTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Opens the devices the settings mean now, where they are not the ones open. A device that fails to open leaves
    /// voice without it until the next change, and says so in the log.
    /// </summary>
    private void Pick(string why)
    {
        lock (_picking)
        {
            if (_disposed) return;
            try
            {
                var echoCancelled = _echoCancellation && _microphone is null && _speaker is null;
                var devices = _devices.List(consoleDefaults: echoCancelled || !_micOpen);
                var microphone = VoiceDevices.Choose(_microphone, devices.Microphones, devices.DefaultMicrophoneId);
                var speaker = VoiceDevices.Choose(_speaker, devices.Speakers, devices.DefaultSpeakerId);

                if (!_micOpen)
                    CloseMicrophone(why);
                else if (new Opened(microphone.Id, echoCancelled) != _openedMicrophone)
                {
                    LogFallback("microphone", _microphone, microphone);
                    if (_echoCancellation && !echoCancelled && _openedMicrophone is null or { EchoCancelled: true })
                        _logger.LogWarning("Voice: echo cancellation needs Default for both the microphone and the speaker " +
                                           "(the echo canceller opens the default pair); capturing without it");
                    OpenMicrophone(microphone, echoCancelled, why);
                }
                if (new Opened(speaker.Id, false) != _openedSpeaker)
                {
                    LogFallback("speaker", _speaker, speaker);
                    OpenSpeaker(speaker, why);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Voice: could not pick the devices ({Why})", why);
            }
        }
    }

    /// <summary>Lets go of the microphone, and gives the session silence in its place.</summary>
    private void CloseMicrophone(string why)
    {
        if (_openedMicrophone == Closed) return;
        _openedMicrophone = Closed;
        _current = null;
        Release(_source.Use(new NoMicrophone()), "microphone");
        _logger.LogInformation("Voice: microphone closed ({Why})", why);
    }

    private void OpenMicrophone(DeviceChoice choice, bool echoCancelled, string why)
    {
        _openedMicrophone = new Opened(choice.Id, echoCancelled);
        if (choice.Id is null)
        {
            _logger.LogWarning("Voice: there is no microphone ({Why})", why);
            _current = null;
            Release(_source.Use(new NoMicrophone()), "microphone");
            return;
        }

        IMicrophone? microphone = null;
        try
        {
            microphone = _devices.OpenMicrophone(new AudioDevice(choice.Id, choice.Name), echoCancelled);
            if (_started) microphone.Start();
        }
        catch (Exception ex)
        {
            Release(microphone, "microphone");
            _openedMicrophone = null;
            _logger.LogError(ex, "Voice: could not open the microphone {Name}", choice.Name);
            return;
        }

        _current = microphone;
        Release(_source.Use(microphone), "microphone");
        _logger.LogInformation("Voice: microphone {Name} ({Kind}, {Capture}; {Why})",
            choice.Name, choice.Pinned ? "chosen" : "default", microphone.Description, why);
    }

    private void OpenSpeaker(DeviceChoice choice, string why)
    {
        _openedSpeaker = new Opened(choice.Id, false);
        if (choice.Id is null)
        {
            _logger.LogWarning("Voice: there is no speaker ({Why})", why);
            Release(_sink.Use(null), "speaker");
            return;
        }

        ISpeaker speaker;
        try
        {
            speaker = _devices.OpenSpeaker(new AudioDevice(choice.Id, choice.Name));
        }
        catch (Exception ex)
        {
            _openedSpeaker = null;
            _logger.LogError(ex, "Voice: could not open the speaker {Name}", choice.Name);
            return;
        }

        Release(_sink.Use(speaker), "speaker");
        _logger.LogInformation("Voice: speaker {Name} ({Kind}; {Why})", choice.Name, choice.Pinned ? "chosen" : "default", why);
    }

    /// <summary>
    /// Lets go of a device voice no longer uses. One that went away can throw as it is stopped (NAudio's WaveOutEvent and
    /// WaveInEvent do): that is logged, and never keeps voice from the device it moves to, or from stopping.
    /// </summary>
    private void Release(object? device, string kind)
    {
        try
        {
            (device as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: letting go of the {Kind} before failed", kind);
        }
    }

    private void LogFallback(string kind, AudioDevice? setting, DeviceChoice choice)
    {
        if (choice.FellBack)
            _logger.LogWarning("Voice: the chosen {Kind} {Name} is not there; using the default ({Default}) until it is back",
                kind, setting?.Name, choice.Name);
    }

    public void Dispose()
    {
        lock (_picking)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Release(_watch, "device watcher");
        _settle.Dispose();
        Release(_source.Complete(), "microphone");
        Release(_sink.Use(null), "speaker");
    }

    /// <summary>With no microphone at all, or the mic closed, the session hears nothing until one opens.</summary>
    private sealed class NoMicrophone : IAudioSource
    {
        private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public AudioFormat Format => AudioFormat.Pcm16kHz;
        public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;
    }
}
