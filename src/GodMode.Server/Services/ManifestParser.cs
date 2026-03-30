using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Parses and validates GodMode manifest files (JSON format).
/// Resolves relative paths against the manifest's directory.
/// </summary>
public class ManifestParser : IManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ILogger<ManifestParser> _logger;

    public ManifestParser(ILogger<ManifestParser> logger)
    {
        _logger = logger;
    }

    public GodModeManifest Parse(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<GodModeManifest>(content, JsonOptions)
                ?? throw new InvalidOperationException("Manifest deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid manifest JSON: {ex.Message}", ex);
        }
    }

    public GodModeManifest ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Manifest file not found: {filePath}");

        var content = File.ReadAllText(filePath);
        var manifest = Parse(content);

        // Resolve relative paths against the manifest's directory
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        return ResolveRelativePaths(manifest, baseDir);
    }

    public string? Validate(GodModeManifest manifest)
    {
        if (manifest.Roots is { Count: > 0 })
        {
            foreach (var (name, root) in manifest.Roots)
            {
                if (string.IsNullOrWhiteSpace(root.Path) && string.IsNullOrWhiteSpace(root.Git))
                    return $"Root '{name}' must specify either 'path' or 'git'.";

                if (!string.IsNullOrWhiteSpace(root.Path) && !string.IsNullOrWhiteSpace(root.Git))
                    return $"Root '{name}' cannot specify both 'path' and 'git'.";
            }
        }

        if (manifest.Profiles is { Count: > 0 } && manifest.Roots != null)
        {
            foreach (var (profileName, profile) in manifest.Profiles)
            {
                if (profile.Roots == null) continue;
                foreach (var rootRef in profile.Roots)
                {
                    if (!manifest.Roots.ContainsKey(rootRef))
                        return $"Profile '{profileName}' references unknown root '{rootRef}'.";
                }
            }
        }

        return null;
    }

    private static GodModeManifest ResolveRelativePaths(GodModeManifest manifest, string baseDir)
    {
        if (manifest.Roots == null) return manifest;

        var resolved = new Dictionary<string, ManifestRoot>();
        foreach (var (name, root) in manifest.Roots)
        {
            if (root.Path != null && !Path.IsPathRooted(root.Path))
            {
                resolved[name] = root with { Path = Path.GetFullPath(Path.Combine(baseDir, root.Path)) };
            }
            else
            {
                resolved[name] = root;
            }
        }

        return manifest with { Roots = resolved };
    }
}
