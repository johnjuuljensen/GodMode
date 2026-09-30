using System.Runtime.InteropServices;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoiceBot.Core.Audio;
using VoiceBot.Providers.Windows;

namespace GodMode.Maui;

/// <summary>
/// Windows' microphones and speakers for voice: listed through Core Audio (endpoint ids, the names Windows shows), and
/// looked up as the WinMM device numbers VoiceBot's <c>NativeAudioSource</c> and <c>NativeAudioSink</c> open
/// (johnjuuljensen/VoiceBot#66 asks for opening by endpoint id). Default is the default communications device; with
/// echo cancellation the console default, which the Voice Capture DSP pairs.
/// </summary>
public sealed class WindowsAudioDevices(ILoggerFactory loggerFactory) : IAudioDevices
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WindowsAudioDevices>();

    public VoiceDeviceList List(bool echoCancelled) => ListDevices(echoCancelled ? Role.Console : Role.Communications);

    IDisposable IAudioDevices.Watch(Action changed) => Watch(changed);

    public IMicrophone OpenMicrophone(AudioDevice device, bool echoCancelled)
    {
        // The DSP takes no device number: it opens the default pair, the one List(echoCancelled) named
        var number = echoCancelled ? -1 : WaveInNumber(device.Id) ?? Missing("microphone", device);
        return new Microphone(new NativeAudioSource(
            deviceNumber: number,
            capture: echoCancelled ? MicCapture.EchoCancelled : MicCapture.WaveIn,
            logger: loggerFactory.CreateLogger<NativeAudioSource>()));
    }

    public ISpeaker OpenSpeaker(AudioDevice device) =>
        new Speaker(new NativeAudioSink(AudioFormat.Pcm16kHz, WaveOutNumber(device.Id) ?? Missing("speaker", device)));

    private int Missing(string kind, AudioDevice device)
    {
        _logger.LogWarning("Voice: WinMM has no device for the {Kind} {Name}; using WinMM's default", kind, device.Name);
        return -1;
    }

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
    public static VoiceDeviceList ListDevices(Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        return new VoiceDeviceList(
            Supported: true,
            Microphones: Active(enumerator, DataFlow.Capture),
            Speakers: Active(enumerator, DataFlow.Render),
            DefaultMicrophoneId: DefaultId(enumerator, DataFlow.Capture, role),
            DefaultSpeakerId: DefaultId(enumerator, DataFlow.Render, role));
    }

    private static List<AudioDevice> Active(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            using (device)
                devices.Add(new AudioDevice(device.ID, device.FriendlyName));
        return devices;
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
        return device.ID;
    }

    /// <summary>The WinMM device number of a capture endpoint, or null when WinMM has none for it.</summary>
    public static int? WaveInNumber(string endpointId) =>
        Number(endpointId, WaveInGetNumDevs(), (device, message, a, b) => WaveInMessage(device, message, a, b));

    /// <summary>The WinMM device number of a render endpoint, or null when WinMM has none for it.</summary>
    public static int? WaveOutNumber(string endpointId) =>
        Number(endpointId, WaveOutGetNumDevs(), (device, message, a, b) => WaveOutMessage(device, message, a, b));

    // mmddk.h: DRV_RESERVED + 17 and + 18, which give a WinMM device's Core Audio endpoint id (Vista and later)
    private const uint DrvQueryFunctionInstanceId = 0x0800 + 17;
    private const uint DrvQueryFunctionInstanceIdSize = 0x0800 + 18;
    private const int MmsyserrNoError = 0;

    private delegate int Message(nint device, uint message, nint a, nint b);

    private static int? Number(string endpointId, int count, Message send)
    {
        for (var number = 0; number < count; number++)
            if (VoiceDevices.SameId(EndpointId(number, send), endpointId))
                return number;
        return null;
    }

    private static string? EndpointId(int number, Message send)
    {
        var sizePtr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (send(number, DrvQueryFunctionInstanceIdSize, sizePtr, 0) != MmsyserrNoError) return null;
            var size = Marshal.ReadInt32(sizePtr);
            if (size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                return send(number, DrvQueryFunctionInstanceId, buffer, size) == MmsyserrNoError
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sizePtr);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs")]
    private static extern int WaveInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern int WaveOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveInMessage")]
    private static extern int WaveInMessage(nint device, uint message, nint a, nint b);

    [DllImport("winmm.dll", EntryPoint = "waveOutMessage")]
    private static extern int WaveOutMessage(nint device, uint message, nint a, nint b);

    /// <summary>
    /// Calls <paramref name="changed"/> when a device comes or goes or a default changes, until disposed. Windows calls the
    /// notification client on its own threads, where no work is to be done: this only signals.
    /// </summary>
    public static IDisposable Watch(Action changed) => new Watcher(changed);

    private sealed class Watcher : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly Action _changed;

        public Watcher(Action changed)
        {
            _changed = changed;
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed();

        // A device's volume or name changing is no reason to pick again
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }
    }
}
