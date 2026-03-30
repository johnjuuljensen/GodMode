using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Creates and validates project root configurations on disk.
/// Writes .godmode-root/ directory structure from RootPreview file maps.
/// </summary>
public class RootCreator
{
    private const string GodModeRootDir = ".godmode-root";
    private readonly ILogger<RootCreator> _logger;

    public RootCreator(ILogger<RootCreator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a root preview. Returns null if valid, error message otherwise.
    /// </summary>
    public string? Validate(RootPreview preview)
    {
        if (preview.Files.Count == 0)
            return "Root must contain at least one file.";

        // Must have a config.json
        if (!preview.Files.ContainsKey("config.json"))
            return "Root must contain a config.json file.";

        // Validate config.json is valid JSON
        try
        {
            JsonDocument.Parse(preview.Files["config.json"]);
        }
        catch (JsonException ex)
        {
            return $"config.json is not valid JSON: {ex.Message}";
        }

        // Validate any other JSON files
        foreach (var (path, content) in preview.Files)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path == "config.json")
                continue;
            try
            {
                JsonDocument.Parse(content);
            }
            catch (JsonException ex)
            {
                return $"{path} is not valid JSON: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a root to disk from a preview. Files are written relative to .godmode-root/.
    /// </summary>
    public void WriteRoot(string rootPath, RootPreview preview)
    {
        var godModeRootPath = Path.Combine(rootPath, GodModeRootDir);
        Directory.CreateDirectory(godModeRootPath);

        foreach (var (relativePath, content) in preview.Files)
        {
            var fullPath = Path.Combine(godModeRootPath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);
        }

        _logger.LogInformation("Wrote root to {RootPath} with {FileCount} files", rootPath, preview.Files.Count);
    }

    /// <summary>
    /// Reads an existing root's .godmode-root/ contents into a RootPreview.
    /// </summary>
    public RootPreview? ReadExistingRoot(string rootPath)
    {
        var godModeRootPath = Path.Combine(rootPath, GodModeRootDir);
        if (!Directory.Exists(godModeRootPath))
            return null;

        var files = new Dictionary<string, string>();
        foreach (var filePath in Directory.GetFiles(godModeRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(godModeRootPath, filePath)
                .Replace('\\', '/');
            files[relativePath] = File.ReadAllText(filePath);
        }

        return new RootPreview(files);
    }
}
