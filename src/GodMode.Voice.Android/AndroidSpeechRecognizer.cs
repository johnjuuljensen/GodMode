using GodMode.Voice.Speech;

namespace GodMode.Voice.Android;

/// <summary>
/// Stub STT for Android. Placeholder for future Android SpeechRecognizer integration.
/// </summary>
public sealed class AndroidSpeechRecognizer : ISpeechRecognizer
{
	public string EngineName => "Android (stub)";

#pragma warning disable CS0067 // Interface-required event — no-op implementation
	public event EventHandler<string>? PartialResultReceived;
#pragma warning restore CS0067

	public IReadOnlyList<string> GetAvailableLanguages() => ["en-US"];

	public Task<bool> IsAvailableAsync() => Task.FromResult(false);

	public Task<string> RecognizeSpeechAsync(CancellationToken ct = default)
		=> Task.FromResult(string.Empty);
}
