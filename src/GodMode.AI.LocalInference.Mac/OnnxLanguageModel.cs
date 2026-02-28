using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace GodMode.AI.LocalInference.Mac;

public sealed class OnnxLanguageModel : ILanguageModel, IDisposable
{
    private OnnxRuntimeGenAIChatClient? _chatClient;
    private readonly AIConfig _config;

    public bool IsLoaded => _chatClient is not null;

    public OnnxLanguageModel()
    {
        _config = AIConfig.Load();
    }

    public async Task InitializeAsync(string modelPath)
    {
        if (IsLoaded) return;

        var fullPath = Path.GetFullPath(modelPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Model directory not found: {fullPath}");

        var configFile = Path.Combine(fullPath, "genai_config.json");
        if (!File.Exists(configFile))
            throw new FileNotFoundException($"genai_config.json not found in: {fullPath}");

        await Task.Run(() =>
        {
            _chatClient = new OnnxRuntimeGenAIChatClient(fullPath);
        });
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Model not loaded. Call InitializeAsync first.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = _config.MaxTokens,
            Temperature = (float)_config.Temperature,
            TopP = 0.9f,
            ResponseFormat = ChatResponseFormatJson.Json
        };

        var response = await _chatClient.GetResponseAsync(messages, options, ct);

        return response.Text ?? string.Empty;
    }

    public void Dispose()
    {
        _chatClient?.Dispose();
    }
}
