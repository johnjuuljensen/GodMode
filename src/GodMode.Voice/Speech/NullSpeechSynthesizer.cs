namespace GodMode.Voice.Speech;

/// <summary>
/// No-op TTS for platforms without native speech synthesis.
/// </summary>
public sealed class NullSpeechSynthesizer : ISpeechSynthesizer
{
    public Task SpeakAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
