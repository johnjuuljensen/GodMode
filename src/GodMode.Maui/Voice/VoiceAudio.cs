using GodMode.Voice;
using VoiceBot.Core.Audio;

namespace GodMode.Maui.Voice;

/// <summary>The microphone and the speaker a voice session uses.</summary>
public interface IVoiceAudio : IDisposable
{
    IAudioSource Source { get; }
    IAudioSink Sink { get; }

    /// <summary>Starts capturing, once the session is ready to hear it.</summary>
    void Start();

    /// <summary>
    /// The settings chose other devices (null for Default): the running session moves to them. Android picks its own
    /// route, and ignores it.
    /// </summary>
    void UseDevices(AudioDevice? microphone, AudioDevice? speaker);
}

/// <summary>What a session asks of this platform's audio.</summary>
/// <param name="EchoCancellation">The setting; Android's voice-communication capture always cancels echo, and ignores it.</param>
/// <param name="Microphone">The microphone the settings chose; null for Default, which follows the platform's.</param>
/// <param name="Speaker">The speaker the settings chose; null for Default.</param>
/// <param name="Lost">
/// The platform took the audio away (Android: a phone call took audio focus); the session is to stop, and say why.
/// Windows never calls it.
/// </param>
public sealed record VoiceAudioRequest(bool EchoCancellation, AudioDevice? Microphone, AudioDevice? Speaker, Action<string> Lost);

/// <summary>This platform's audio for voice, where it has any: Windows (issue #285) and Android (issue #286).</summary>
public static class VoiceAudio
{
    /// <summary>
    /// Opens the microphone and speaker the request names, or the defaults; null where voice is not available.
    /// Asynchronous for Android, which asks for the microphone and starts its foreground service first.
    /// </summary>
    public static Func<VoiceAudioRequest, Task<IVoiceAudio>>? Open { get; } =
#if WINDOWS
        request => Task.FromResult<IVoiceAudio>(new WindowsVoiceAudio(request));
#elif ANDROID
        AndroidVoiceAudio.OpenAsync;
#else
        null;
#endif

    /// <summary>The microphones and speakers the settings can choose from (<c>voice.devices</c>): Windows only.</summary>
    public static VoiceDeviceList Devices() =>
#if WINDOWS
        WindowsAudioDevices.ListDevices(VoiceBot.Providers.Windows.AudioDeviceRole.Communications);
#else
        VoiceDeviceList.None;
#endif
}
