using GodMode.Voice;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;
using VoiceBot.Providers.Windows;

namespace GodMode.Maui;

/// <summary>
/// Windows' microphones and speakers for voice, through VoiceBot.Providers.Windows: listed by Core Audio endpoint id and
/// the name Windows shows (VoiceBot's <c>WindowsAudioDevices</c>), opened by that id (<c>NativeAudioSource</c> and
/// <c>NativeAudioSink</c>, johnjuuljensen/VoiceBot#66), and watched with its <c>AudioEndpointWatcher</c>. Default is
/// the default communications device; with echo cancellation the console default, which the Voice Capture DSP pairs.
/// </summary>
public sealed class WindowsAudioDevices(ILoggerFactory loggerFactory) : IAudioDevices
{
    public VoiceDeviceList List(bool echoCancelled) =>
        ListDevices(echoCancelled ? AudioDeviceRole.Console : AudioDeviceRole.Communications);

    /// <summary>Windows' notifications; if it will not give them, voice keeps the devices it opened, and says so.</summary>
    public IDisposable Watch(Action changed)
    {
        try
        {
            return new AudioEndpointWatcher(changed);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            loggerFactory.CreateLogger<WindowsAudioDevices>().LogWarning(ex, "Voice: Windows gives no device notifications; the devices opened now are kept");
            return new NotWatching();
        }
    }

    private sealed class NotWatching : IDisposable
    {
        public void Dispose() { }
    }

    /// <remarks>An id that is no longer an active device throws: <see cref="FollowingAudio"/> logs it and picks again at the next change.</remarks>
    public IMicrophone OpenMicrophone(AudioDevice device, bool echoCancelled) =>
        // The DSP is left on the default console pair, the one List(echoCancelled) named
        new Microphone(new NativeAudioSource(
            capture: echoCancelled ? MicCapture.EchoCancelled : MicCapture.WaveIn,
            logger: loggerFactory.CreateLogger<NativeAudioSource>(),
            endpointId: echoCancelled ? null : device.Id));

    public ISpeaker OpenSpeaker(AudioDevice device) =>
        new Speaker(new NativeAudioSink(AudioFormat.Pcm16kHz, endpointId: device.Id));

    private sealed class Microphone(NativeAudioSource source) : IMicrophone
    {
        public AudioFormat Format => source.Format;
        public System.Threading.Channels.ChannelReader<ReadOnlyMemory<byte>> Audio => source.Audio;
        public string Description => source.Description;
        public void Start() => source.Start();
        public void Dispose() => source.Dispose();
    }

    private sealed class Speaker(NativeAudioSink sink) : ISpeaker
    {
        public AudioFormat Format => sink.Format;
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => sink.SendAudioAsync(audio, ct);
        public Task SendStatusAsync(string message, CancellationToken ct) => sink.SendStatusAsync(message, ct);
        public Task InterruptAsync(CancellationToken ct) => sink.InterruptAsync(ct);
        public void Dispose() => sink.Dispose();
    }

    /// <summary>The active devices, and the default of each kind in <paramref name="role"/> (voice's: communications).</summary>
    public static VoiceDeviceList ListDevices(AudioDeviceRole role) => new(
        Supported: true,
        Microphones: Devices(VoiceBot.Providers.Windows.WindowsAudioDevices.Microphones()),
        Speakers: Devices(VoiceBot.Providers.Windows.WindowsAudioDevices.Speakers()),
        DefaultMicrophoneId: VoiceBot.Providers.Windows.WindowsAudioDevices.DefaultMicrophoneId(role),
        DefaultSpeakerId: VoiceBot.Providers.Windows.WindowsAudioDevices.DefaultSpeakerId(role));

    private static List<AudioDevice> Devices(IEnumerable<AudioEndpoint> endpoints) =>
        [.. endpoints.Select(e => new AudioDevice(e.Id, e.Name))];
}
