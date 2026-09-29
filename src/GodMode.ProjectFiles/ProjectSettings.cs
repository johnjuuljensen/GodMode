using System.Text;
using System.Text.Json;

namespace GodMode.ProjectFiles;

/// <summary>
/// A session's settings, persisted to settings.json in its state folder (<c>.godmode/sessions/{id}/</c>).
/// Survives process restarts and server recovery.
/// </summary>
/// <param name="DangerouslySkipPermissions">
/// What the create asked for. A launch honours it only while the project's root allows it: the
/// session can write this file, so it cannot be what decides.
/// </param>
/// <param name="PermissionMode">The root's permission mode when the project was created, kept for its resumes.</param>
/// <param name="SharedFolder">
/// Whether the session was created by an action whose sessions share their working folder
/// (<c>sharedFolder</c>): its delete then removes only its state, never the folder. Kept here so a
/// root config that changes later does not turn a shared workspace into one a delete removes.
/// </param>
public record ProjectSettings(
    bool DangerouslySkipPermissions = false,
    string? ActionName = null,
    string? PermissionMode = null,
    bool SharedFolder = false
)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The session's settings; the defaults when its settings.json is missing or cannot be read.</summary>
    public static ProjectSettings Load(string statePath)
    {
        TryLoad(statePath, out var settings);
        return settings;
    }

    /// <summary>
    /// The session's settings, and whether its settings.json was there and could be read: when it was
    /// not, <paramref name="settings"/> is the defaults, and a caller that must not guess (whether the
    /// session shares its folder) can tell.
    /// </summary>
    public static bool TryLoad(string statePath, out ProjectSettings settings)
    {
        settings = new ProjectSettings();
        var path = GetSettingsPath(statePath);
        if (!File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (JsonSerializer.Deserialize<ProjectSettings>(json, JsonOptions) is not { } read) return false;
            settings = read;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(string statePath)
    {
        var path = GetSettingsPath(statePath);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string GetSettingsPath(string statePath) =>
        Path.Combine(statePath, "settings.json");
}
