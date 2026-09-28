using VoiceBot.Core.Audio;

namespace GodMode.Maui.Voice;

/// <summary>The microphone and the speaker a voice session uses.</summary>
public interface IVoiceAudio : IDisposable
{
    IAudioSource Source { get; }
    IAudioSink Sink { get; }

    /// <summary>Starts capturing, once the session is ready to hear it.</summary>
    void Start();
}

/// <summary>What a session asks of this platform's audio.</summary>
/// <param name="EchoCancellation">The setting; Android's voice-communication capture always cancels echo, and ignores it.</param>
/// <param name="Lost">
/// The platform took the audio away (Android: a phone call took audio focus); the session is to stop, and say why.
/// Windows never calls it.
/// </param>
public sealed record VoiceAudioRequest(bool EchoCancellation, Action<string> Lost);

/// <summary>This platform's audio for voice, where it has any: Windows (issue #285) and Android (issue #286).</summary>
public static class VoiceAudio
{
    /// <summary>
    /// Opens the default microphone and speaker; null where voice is not available. Asynchronous for Android, which asks
    /// for the microphone and starts its foreground service first.
    /// </summary>
    public static Func<VoiceAudioRequest, Task<IVoiceAudio>>? Open { get; } =
#if WINDOWS
        request => Task.FromResult<IVoiceAudio>(new WindowsVoiceAudio(request.EchoCancellation));
#elif ANDROID
        AndroidVoiceAudio.OpenAsync;
#else
        null;
#endif
}
