using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace GodMode.AI.LocalInference.Windows;

/// <summary>
/// Factory that creates an IChatClient backed by Phi-4-mini via DirectML (OnnxRuntimeGenAI).
/// </summary>
public sealed class Phi4ChatClientFactory : IChatClientFactory
{
    public async Task<IChatClient> CreateAsync(AIConfig config)
    {
        var modelPath = config.ModelPath
            ?? throw new InvalidOperationException("No model path configured for DirectML provider.");

        var fullPath = Path.GetFullPath(modelPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Model directory not found: {fullPath}");

        var configFile = Path.Combine(fullPath, "genai_config.json");
        if (!File.Exists(configFile))
            throw new FileNotFoundException($"genai_config.json not found in: {fullPath}");

        return await Task.Run(() => new OnnxRuntimeGenAIChatClient(fullPath));
    }
}
