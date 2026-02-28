namespace GodMode.Voice.Speech;

public interface ISpeechRecognizer
{
    string EngineName { get; }
    Task<bool> IsAvailableAsync();
    Task<string> RecognizeSpeechAsync(CancellationToken ct = default);
    IReadOnlyList<string> GetAvailableLanguages();
    event EventHandler<string>? PartialResultReceived;
}
