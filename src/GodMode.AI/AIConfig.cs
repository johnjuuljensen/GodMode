using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.AI;

/// <summary>
/// AI model configuration. Reads/writes ~/.godmode/inference.json
/// (shared file with VoiceConfig — each class owns its keys).
/// </summary>
public sealed class AIConfig
{
    [JsonPropertyName("phi4_model_path")]
    public string? ModelPath { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 256;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("tiers")]
    public Dictionary<InferenceTier, TierConfig>? Tiers { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode");

    public static string ConfigPath =>
        Path.Combine(ConfigDir, "inference.json");

    public static AIConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AIConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AIConfig>(json, JsonOptions) ?? new AIConfig();
    }

    /// <summary>
    /// Saves AI config fields by merging into the existing inference.json
    /// to preserve voice config keys.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        Dictionary<string, JsonElement>? existing = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var raw = File.ReadAllText(ConfigPath);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
            }
            catch { /* corrupted file — overwrite */ }
        }

        existing ??= new Dictionary<string, JsonElement>();

        // Merge our keys
        MergeOptional(existing, "phi4_model_path", ModelPath);
        MergeOptional(existing, "api_key", ApiKey);
        MergeOptional(existing, "provider", Provider);
        MergeOptional(existing, "model", Model);

        existing["max_tokens"] = JsonSerializer.SerializeToElement(MaxTokens);
        existing["temperature"] = JsonSerializer.SerializeToElement(Temperature);

        if (Tiers is not null && Tiers.Count > 0)
            existing["tiers"] = JsonSerializer.SerializeToElement(Tiers, JsonOptions);
        else
            existing.Remove("tiers");

        var json = JsonSerializer.Serialize(existing, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static void MergeOptional(Dictionary<string, JsonElement> existing, string key, string? value)
    {
        if (value is not null)
            existing[key] = JsonSerializer.SerializeToElement(value);
        else
            existing.Remove(key);
    }
}
