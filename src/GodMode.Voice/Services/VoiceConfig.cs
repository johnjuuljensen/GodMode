using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.Voice.Services;

/// <summary>
/// Voice/speech configuration. Reads/writes ~/.godmode/inference.json
/// (shared file with AIConfig — each class owns its keys).
/// </summary>
public sealed class VoiceConfig
{
    [JsonPropertyName("whisper_model_path")]
    public string? WhisperModelPath { get; set; }

    [JsonPropertyName("speech_language")]
    public string SpeechLanguage { get; set; } = "en-US";

    [JsonPropertyName("prefer_offline_stt")]
    public bool PreferOfflineStt { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode");

    public static string ConfigPath =>
        Path.Combine(ConfigDir, "inference.json");

    public static VoiceConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new VoiceConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<VoiceConfig>(json, JsonOptions) ?? new VoiceConfig();
    }

    /// <summary>
    /// Saves voice config fields by merging into the existing inference.json
    /// to preserve AI config keys.
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
        if (WhisperModelPath is not null)
            existing["whisper_model_path"] = JsonSerializer.SerializeToElement(WhisperModelPath);
        else
            existing.Remove("whisper_model_path");

        existing["speech_language"] = JsonSerializer.SerializeToElement(SpeechLanguage);
        existing["prefer_offline_stt"] = JsonSerializer.SerializeToElement(PreferOfflineStt);

        var json = JsonSerializer.Serialize(existing, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
