using GodMode.Maui.Voice;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;

namespace GodMode.Maui;

/// <summary>
/// The microphone and speaker for voice, through VoiceBot.Providers.Windows (<see cref="WindowsAudioDevices"/>): plain
/// WaveIn capture, or Windows' Voice Capture DSP when echo cancellation is on (off by default until it is measured on
/// laptop speakers). Each is the device the settings chose while it is there, else Windows' default communications
/// device, followed as devices come and go (<see cref="FollowingAudio"/>).
/// </summary>
public sealed class WindowsVoiceAudio(VoiceAudioRequest request) : IVoiceAudio
{
    private readonly FollowingAudio _audio = new(
        new WindowsAudioDevices(MauiProgram.LoggerFactory), request.EchoCancellation, request.Microphone, request.Speaker,
        MauiProgram.LoggerFactory.CreateLogger<WindowsVoiceAudio>());

    public IAudioSource Source => _audio.Source;
    public IAudioSink Sink => _audio.Sink;

    public void Start() => _audio.Start();

    public void UseDevices(AudioDevice? microphone, AudioDevice? speaker) => _audio.UseDevices(microphone, speaker);

    public void Dispose() => _audio.Dispose();
}
