using GodMode.Maui.Voice;
using VoiceBot.Core.Audio;
using VoiceBot.Providers.Windows;

namespace GodMode.Maui;

/// <summary>
/// The default microphone and speaker, through VoiceBot.Providers.Windows: plain WaveIn capture, or Windows' Voice
/// Capture DSP when echo cancellation is on (off by default until it is measured on laptop speakers).
/// </summary>
public sealed class WindowsVoiceAudio(bool echoCancellation) : IVoiceAudio
{
    private readonly NativeAudioSource _source = new(capture: echoCancellation ? MicCapture.EchoCancelled : MicCapture.WaveIn);
    private readonly NativeAudioSink _sink = new();

    public IAudioSource Source => _source;
    public IAudioSink Sink => _sink;

    public void Start() => _source.Start();

    public void Dispose()
    {
        _source.Dispose();
        _sink.Dispose();
    }
}
