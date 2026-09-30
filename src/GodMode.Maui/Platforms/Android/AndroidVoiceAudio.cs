using Android.Content;
using Android.Media;
using GodMode.Maui.Voice;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;
using VoiceBot.Providers.Android;

namespace GodMode.Maui;

/// <summary>
/// The phone's microphone and speaker for voice, through VoiceBot.Providers.Android (voice-communication capture and
/// playback, so the platform's echo cancellation applies). Around them the app does what VoiceBot leaves to its host:
/// the microphone foreground service (<see cref="VoiceService"/>), which keeps listening with the screen off; and audio
/// focus, whose loss to a call stops voice. It also sets the audio mode and the communication device itself, in place of
/// the sink's own (johnjuuljensen/VoiceBot#31), which picks the route once: the app's prefers a headset, and picks
/// again when one comes or goes.
/// </summary>
public sealed class AndroidVoiceAudio : IVoiceAudio
{
    private readonly AudioManager _manager;
    private readonly AudioRoute _route;
    private readonly Focus _focus;
    private readonly AndroidAudioSource _source;
    private readonly AndroidAudioSink _sink;
    private bool _disposed;

    private AndroidVoiceAudio(AudioManager manager, AudioRoute route, Focus focus, AndroidAudioSource source, AndroidAudioSink sink)
    {
        _manager = manager;
        _route = route;
        _focus = focus;
        _source = source;
        _sink = sink;
    }

    public IAudioSource Source => _source;
    public IAudioSink Sink => _sink;

    /// <summary>
    /// Asks for RECORD_AUDIO (the first time), starts <see cref="VoiceService"/> while the app is still in front (Android
    /// 14 lets a microphone service start from nowhere else), then takes the audio. Anything that fails undoes the rest.
    /// </summary>
    public static async Task<IVoiceAudio> OpenAsync(VoiceAudioRequest request)
    {
        var logger = MauiProgram.LoggerFactory.CreateLogger<AndroidVoiceAudio>();
        if (await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Microphone>) != PermissionStatus.Granted)
            throw new PermissionException("Voice needs the microphone: allow it for GodMode in the phone's settings");

        await VoiceService.StartAsync();
        var manager = (AudioManager)Platform.AppContext.GetSystemService(Context.AudioService)!;
        AudioRoute? route = null;
        Focus? focus = null;
        AndroidAudioSource? source = null;
        try
        {
            route = AudioRoute.Take(manager, logger);
            focus = Focus.Take(manager, request.Lost, logger);
            source = new AndroidAudioSource();
            var sink = new AndroidAudioSink(communicationMode: false, logger: MauiProgram.LoggerFactory.CreateLogger<AndroidAudioSink>());
            logger.LogInformation("Android voice audio open: echo canceller {Echo}, noise suppressor {Noise}",
                source.EchoCancellerEnabled ? "on" : "not available", source.NoiseSuppressorEnabled ? "on" : "not available");
            return new AndroidVoiceAudio(manager, route, focus, source, sink);
        }
        catch
        {
            source?.Dispose();
            focus?.Release(manager);
            route?.Restore();
            VoiceService.Stop();
            throw;
        }
    }

    public void Start() => _source.Start();

    /// <summary>Android routes voice itself (<see cref="AudioRoute"/>): the settings offer no devices here.</summary>
    public void UseDevices(AudioDevice? microphone, AudioDevice? speaker) { }

    /// <summary>Lets go in the reverse order of taking: audio, focus, route, and the service last.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
        _sink.Dispose();
        _focus.Release(_manager);
        _route.Restore();
        VoiceService.Stop();
    }

    /// <summary>
    /// MODE_IN_COMMUNICATION and the communication device: the speaker, or a headset when one is connected, picked again
    /// when a device comes or goes. Without them a phone may play into the earpiece, and cancel echo less well.
    /// <see cref="Restore"/> puts the mode back as it was and withdraws the app's device choice.
    /// </summary>
    private sealed class AudioRoute : AudioDeviceCallback
    {
        /// <summary>Preferred in this order over the speaker (Android 12+, which can route to any of them).</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
        private static readonly AudioDeviceType[] Headsets =
        [
            AudioDeviceType.WiredHeadset, AudioDeviceType.UsbHeadset, AudioDeviceType.BleHeadset, AudioDeviceType.BluetoothSco,
            AudioDeviceType.WiredHeadphones,
        ];

        /// <summary>A headset Android 7 to 11 routes to by itself in communication mode, taking over from the earpiece.</summary>
#pragma warning disable CA1416 // USB headsets came with Android 8; Android 7 lists none, so the value is only never matched there
        private static readonly AudioDeviceType[] WiredHeadsets =
            [AudioDeviceType.WiredHeadset, AudioDeviceType.WiredHeadphones, AudioDeviceType.UsbHeadset];
#pragma warning restore CA1416

        private readonly AudioManager _manager;
        private readonly ILogger _logger;
        private readonly Mode _mode;
        private readonly bool _speakerphone;

        private AudioRoute(AudioManager manager, ILogger logger)
        {
            _manager = manager;
            _logger = logger;
            _mode = manager.Mode;
#pragma warning disable CA1422 // Android 12 replaced the speakerphone with the communication device; below it, it is the way
            _speakerphone = manager.SpeakerphoneOn;
#pragma warning restore CA1422
        }

        public static AudioRoute Take(AudioManager manager, ILogger logger)
        {
            var route = new AudioRoute(manager, logger);
            manager.Mode = Mode.InCommunication;
            route.Pick();
            manager.RegisterAudioDeviceCallback(route, null);
            logger.LogInformation("Audio mode {Mode} (was {Was})", manager.Mode, route._mode);
            return route;
        }

        public void Restore()
        {
            _manager.UnregisterAudioDeviceCallback(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                _manager.ClearCommunicationDevice();
            else
#pragma warning disable CA1422
                _manager.SpeakerphoneOn = _speakerphone;
#pragma warning restore CA1422
            _manager.Mode = _mode;
            _logger.LogInformation("Audio mode restored to {Mode}", _manager.Mode);
        }

        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => Pick();
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => Pick();

        private void Pick()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var devices = _manager.AvailableCommunicationDevices;
                var device = Headsets.Select(type => devices.FirstOrDefault(d => d.Type == type)).FirstOrDefault(d => d is not null)
                             ?? devices.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinSpeaker);
                if (device is null)
                    _logger.LogWarning("No speaker or headset to talk through; Android picks");
                else
                    _logger.LogInformation("Communication device {Type} ({Name}): {Set}",
                        device.Type, device.ProductName, _manager.SetCommunicationDevice(device) ? "set" : "refused");
                return;
            }

            var headset = _manager.GetDevices(GetDevicesTargets.Outputs)!.Any(d => WiredHeadsets.Contains(d.Type));
#pragma warning disable CA1422
            _manager.SpeakerphoneOn = !headset;
#pragma warning restore CA1422
            _logger.LogInformation("Speakerphone {On}", headset ? "off (headset)" : "on");
        }
    }

    /// <summary>
    /// Audio focus for the whole conversation. A call (or anything else taking it for good or for a while) ends voice:
    /// the microphone would hear the call, and the bot would talk over it. Ducking for a moment (a navigation prompt) does not.
    /// </summary>
    private sealed class Focus : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly Action<string> _lost;
        private readonly ILogger _logger;
        private AudioFocusRequestClass? _request;
        private int _gone;

        private Focus(Action<string> lost, ILogger logger)
        {
            _lost = lost;
            _logger = logger;
        }

        public static Focus Take(AudioManager manager, Action<string> lost, ILogger logger)
        {
            var focus = new Focus(lost, logger);
            AudioFocusRequest result;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var attributes = new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.VoiceCommunication)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!;
                focus._request = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                    .SetAudioAttributes(attributes)
                    .SetOnAudioFocusChangeListener(focus)
                    .Build()!;
                result = manager.RequestAudioFocus(focus._request);
            }
            else
            {
#pragma warning disable CA1422 // Android 8 replaced it with a focus request
                result = manager.RequestAudioFocus(focus, Android.Media.Stream.VoiceCall, AudioFocus.Gain);
#pragma warning restore CA1422
            }
            if (result != AudioFocusRequest.Granted)
            {
                focus.Dispose();
                throw new InvalidOperationException("A call has the microphone and speaker: try again after it");
            }
            return focus;
        }

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            _logger.LogInformation("Audio focus: {Change}", focusChange);
            if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient && Interlocked.Exchange(ref _gone, 1) == 0)
                _lost("A call took the microphone and speaker, so voice stopped. Start it again after the call.");
        }

        public void Release(AudioManager manager)
        {
            Interlocked.Exchange(ref _gone, 1);
            if (_request is not null && OperatingSystem.IsAndroidVersionAtLeast(26))
                manager.AbandonAudioFocusRequest(_request);
            else
#pragma warning disable CA1422
                manager.AbandonAudioFocus(this);
#pragma warning restore CA1422
            Dispose();
        }
    }
}
