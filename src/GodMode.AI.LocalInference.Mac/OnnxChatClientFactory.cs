using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace GodMode.AI.LocalInference.Mac;

/// <summary>
/// Factory that creates an IChatClient backed by ONNX Runtime GenAI on CPU (macOS).
/// </summary>
public sealed class OnnxChatClientFactory : IChatClientFactory
{
    public async Task<IChatClient> CreateAsync(AIConfig config)
    {
        var modelPath = config.ModelPath
            ?? throw new InvalidOperationException("No model path configured for CPU provider.");

        var fullPath = Path.GetFullPath(modelPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Model directory not found: {fullPath}");

        var configFile = Path.Combine(fullPath, "genai_config.json");
        if (!File.Exists(configFile))
            throw new FileNotFoundException($"genai_config.json not found in: {fullPath}");

        return await Task.Run(() => new OnnxRuntimeGenAIChatClient(fullPath));
    }
}
