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

    [JsonPropertyName("npu_model_path")]
    public string? NpuModelPath { get; set; }

    [JsonPropertyName("execution_provider")]
    public string? ExecutionProvider { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 256;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

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
        if (ModelPath is not null)
            existing["phi4_model_path"] = JsonSerializer.SerializeToElement(ModelPath);
        else
            existing.Remove("phi4_model_path");

        if (NpuModelPath is not null)
            existing["npu_model_path"] = JsonSerializer.SerializeToElement(NpuModelPath);
        else
            existing.Remove("npu_model_path");

        if (ExecutionProvider is not null)
            existing["execution_provider"] = JsonSerializer.SerializeToElement(ExecutionProvider);
        else
            existing.Remove("execution_provider");

        existing["max_tokens"] = JsonSerializer.SerializeToElement(MaxTokens);
        existing["temperature"] = JsonSerializer.SerializeToElement(Temperature);

        if (Tiers is not null && Tiers.Count > 0)
            existing["tiers"] = JsonSerializer.SerializeToElement(Tiers, JsonOptions);
        else
            existing.Remove("tiers");

        var json = JsonSerializer.Serialize(existing, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
