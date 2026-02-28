namespace GodMode.AI;

/// <summary>
/// No-op language model for platforms without local inference.
/// </summary>
public sealed class NullLanguageModel : ILanguageModel
{
    public bool IsLoaded => false;

    public Task<string> GenerateAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task InitializeAsync(string modelPath)
        => Task.CompletedTask;
}
