using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Writes .godmode-root/ directory structure from a RootPreview.
/// Validates that the generated config is parseable.
/// </summary>
public class RootCreator
{
    private readonly ILogger<RootCreator> _logger;

    public RootCreator(ILogger<RootCreator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Writes root files from a preview to the target directory.
    /// Creates the .godmode-root/ subdirectory structure.
    /// Sets execute permissions on script files (Unix).
    /// </summary>
    public void WriteRoot(string rootPath, RootPreview preview)
    {
        var godmodeRootPath = Path.Combine(rootPath, ".godmode-root");
        Directory.CreateDirectory(godmodeRootPath);

        foreach (var (relativePath, content) in preview.Files)
        {
            var fullPath = Path.Combine(godmodeRootPath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null) Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);

            // Set execute permission on script files (Unix)
            if (relativePath.EndsWith(".sh") && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(fullPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set execute permission on {Path}", fullPath);
                }
            }
        }

        _logger.LogInformation("Wrote {FileCount} root files to {Path}", preview.Files.Count, godmodeRootPath);
    }

    /// <summary>
    /// Validates a RootPreview by checking that config.json is parseable.
    /// Returns a validation error message, or null if valid.
    /// </summary>
    public string? Validate(RootPreview preview)
    {
        if (!preview.Files.ContainsKey("config.json"))
            return "Root must contain a config.json file";

        try
        {
            JsonSerializer.Deserialize<JsonElement>(preview.Files["config.json"]);
        }
        catch (JsonException ex)
        {
            return $"config.json is not valid JSON: {ex.Message}";
        }

        // Validate any schema.json files
        foreach (var (path, content) in preview.Files)
        {
            if (!path.EndsWith("schema.json")) continue;
            try
            {
                var schema = JsonSerializer.Deserialize<JsonElement>(content);
                if (!schema.TryGetProperty("type", out _))
                    return $"{path}: JSON Schema must have a 'type' property";
            }
            catch (JsonException ex)
            {
                return $"{path} is not valid JSON: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an existing root's files into a RootPreview for editing.
    /// </summary>
    public RootPreview ReadExistingRoot(string rootPath)
    {
        var godmodeRootPath = Path.Combine(rootPath, ".godmode-root");
        var files = new Dictionary<string, string>();

        if (!Directory.Exists(godmodeRootPath))
            return new RootPreview(files);

        foreach (var filePath in Directory.GetFiles(godmodeRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(godmodeRootPath, filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".json" or ".sh" or ".ps1" or ".cmd" or ".bat" or ".md" or ".txt" or "")
            {
                files[relativePath] = File.ReadAllText(filePath);
            }
        }

        return new RootPreview(files);
    }
}
