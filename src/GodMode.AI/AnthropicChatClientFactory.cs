using Anthropic;
using Microsoft.Extensions.AI;

namespace GodMode.AI;

/// <summary>
/// Creates an IChatClient backed by the Anthropic API (Claude models).
/// </summary>
public sealed class AnthropicChatClientFactory : IChatClientFactory
{
    public Task<IChatClient> CreateAsync(AIConfig config)
    {
        var apiKey = config.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException(
                "Anthropic API key not configured. Set 'api_key' in inference.json or the ANTHROPIC_API_KEY environment variable.");

        var client = new AnthropicClient() { ApiKey = apiKey };
        return Task.FromResult(client.AsIChatClient(config.Model ?? "claude-sonnet-4-20250514"));
    }
}
