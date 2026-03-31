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

        if (manifest.Profiles is { Count: > 0 })
        {
            foreach (var (name, profile) in manifest.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.Path) && !string.IsNullOrWhiteSpace(profile.Git))
                    return $"Profile '{name}' cannot specify both 'path' and 'git'.";
            }
        }

        return null;
    }

    private static GodModeManifest ResolveRelativePaths(GodModeManifest manifest, string baseDir)
    {
        Dictionary<string, ManifestRoot>? resolvedRoots = null;
        if (manifest.Roots != null)
        {
            resolvedRoots = new Dictionary<string, ManifestRoot>();
            foreach (var (name, root) in manifest.Roots)
            {
                resolvedRoots[name] = root.Path != null && !Path.IsPathRooted(root.Path)
                    ? root with { Path = Path.GetFullPath(Path.Combine(baseDir, root.Path)) }
                    : root;
            }
        }

        Dictionary<string, ManifestProfile>? resolvedProfiles = null;
        if (manifest.Profiles != null)
        {
            resolvedProfiles = new Dictionary<string, ManifestProfile>();
            foreach (var (name, profile) in manifest.Profiles)
            {
                resolvedProfiles[name] = profile.Path != null && !Path.IsPathRooted(profile.Path)
                    ? profile with { Path = Path.GetFullPath(Path.Combine(baseDir, profile.Path)) }
                    : profile;
            }
        }

        return manifest with { Roots = resolvedRoots ?? manifest.Roots, Profiles = resolvedProfiles ?? manifest.Profiles };
    }
}
