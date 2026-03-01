using System.Text.Json.Serialization;

namespace GodMode.AI;

/// <summary>
/// Per-tier inference configuration.
/// </summary>
public sealed record TierConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "auto";

    [JsonPropertyName("model_path")]
    public string? ModelPath { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }
}
