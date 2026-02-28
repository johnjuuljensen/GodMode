using GodMode.Voice.Speech;

namespace GodMode.Voice.Mac;

/// <summary>
/// Stub TTS for macOS. Placeholder for future AVSpeechSynthesizer integration.
/// </summary>
public sealed class MacSpeechSynthesizer : ISpeechSynthesizer
{
    public Task SpeakAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
