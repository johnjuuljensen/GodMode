using GodMode.Voice.Speech;

namespace GodMode.Voice.Mac;

/// <summary>
/// Stub STT for macOS. Placeholder for future SFSpeechRecognizer integration.
/// </summary>
public sealed class MacSpeechRecognizer : ISpeechRecognizer
{
    public string EngineName => "macOS (stub)";

#pragma warning disable CS0067 // Interface-required event — no-op implementation
    public event EventHandler<string>? PartialResultReceived;
#pragma warning restore CS0067

    public IReadOnlyList<string> GetAvailableLanguages() => ["en-US"];

    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public Task<string> RecognizeSpeechAsync(CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
