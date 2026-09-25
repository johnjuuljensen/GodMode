using System.Text;
using System.Text.Json;

namespace GodMode.ProjectFiles;

/// <summary>
/// Per-project settings persisted to .godmode/settings.json.
/// Survives process restarts and server recovery.
/// </summary>
/// <param name="DangerouslySkipPermissions">
/// What the create asked for. A launch honours it only while the project's root allows it: the
/// session can write this file, so it cannot be what decides.
/// </param>
/// <param name="PermissionMode">The root's permission mode when the project was created, kept for its resumes.</param>
public record ProjectSettings(
    bool DangerouslySkipPermissions = false,
    string? ActionName = null,
    string? PermissionMode = null
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
