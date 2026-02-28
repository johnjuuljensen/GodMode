using System.Text;
using System.Text.Json;

namespace GodMode.ProjectFiles;

/// <summary>
/// Per-project settings persisted to .godmode/settings.json.
/// Survives process restarts and server recovery.
/// </summary>
public record ProjectSettings(
    bool DangerouslySkipPermissions = false,
    string? ActionName = null
)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ProjectSettings Load(string projectPath)
    {
        var path = GetSettingsPath(projectPath);
        if (!File.Exists(path))
            return new ProjectSettings();

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ProjectSettings>(json, JsonOptions) ?? new ProjectSettings();
        }
        catch
        {
            return new ProjectSettings();
        }
    }

    public void Save(string projectPath)
    {
        var path = GetSettingsPath(projectPath);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string GetSettingsPath(string projectPath) =>
        Path.Combine(projectPath, ".godmode", "settings.json");
}
