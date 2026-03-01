using GodMode.Voice.Speech;

namespace GodMode.Voice.Android;

/// <summary>
/// Stub TTS for Android. Placeholder for future Android TextToSpeech integration.
/// </summary>
public sealed class AndroidSpeechSynthesizer : ISpeechSynthesizer
{
	public Task SpeakAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
	public Task StopAsync() => Task.CompletedTask;
}
