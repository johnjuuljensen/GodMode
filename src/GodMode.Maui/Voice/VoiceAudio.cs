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

/// <summary>This platform's audio for voice, where it has any: Windows only, for now (issue #285).</summary>
public static class VoiceAudio
{
    /// <summary>Opens the default microphone and speaker; null where voice is not available.</summary>
    public static Func<bool, IVoiceAudio>? Open { get; } =
#if WINDOWS
        echoCancellation => new WindowsVoiceAudio(echoCancellation);
#else
        null;
#endif
}
