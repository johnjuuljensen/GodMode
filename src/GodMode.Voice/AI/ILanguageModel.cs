namespace GodMode.Voice.AI;

public interface ILanguageModel
{
    Task<string> GenerateAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
    Task InitializeAsync(string modelPath);
    bool IsLoaded { get; }
}
