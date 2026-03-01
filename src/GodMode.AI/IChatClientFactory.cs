using Microsoft.Extensions.AI;

namespace GodMode.AI;

/// <summary>
/// Factory for creating IChatClient instances from configuration.
/// Implemented by both local (ONNX) and remote (Anthropic) providers.
/// </summary>
public interface IChatClientFactory
{
    Task<IChatClient> CreateAsync(AIConfig config);
}
