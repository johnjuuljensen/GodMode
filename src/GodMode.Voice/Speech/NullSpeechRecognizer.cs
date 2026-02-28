namespace GodMode.Voice.Speech;

/// <summary>
/// No-op speech recognizer for platforms without STT support.
/// </summary>
public sealed class NullSpeechRecognizer : ISpeechRecognizer
{
    public string EngineName => "None";

#pragma warning disable CS0067 // Interface-required event — no-op implementation
    public event EventHandler<string>? PartialResultReceived;
#pragma warning restore CS0067

    public IReadOnlyList<string> GetAvailableLanguages() => ["en-US"];

    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public Task<string> RecognizeSpeechAsync(CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
