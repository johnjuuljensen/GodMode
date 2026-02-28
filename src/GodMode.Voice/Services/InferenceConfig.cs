using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.Voice.Services;

public sealed class InferenceConfig
{
    [JsonPropertyName("phi4_model_path")]
    public string? Phi4ModelPath { get; set; }

    [JsonPropertyName("whisper_model_path")]
    public string? WhisperModelPath { get; set; }

    [JsonPropertyName("npu_model_path")]
    public string? NpuModelPath { get; set; }

    [JsonPropertyName("execution_provider")]
    public string? ExecutionProvider { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 256;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

    [JsonPropertyName("speech_language")]
    public string SpeechLanguage { get; set; } = "en-US";

    [JsonPropertyName("prefer_offline_stt")]
    public bool PreferOfflineStt { get; set; } = true;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode");

    public static string ConfigPath =>
        Path.Combine(ConfigDir, "inference.json");

    public static InferenceConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new InferenceConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<InferenceConfig>(json, _jsonOptions) ?? new InferenceConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
