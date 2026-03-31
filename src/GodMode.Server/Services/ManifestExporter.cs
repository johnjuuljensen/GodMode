using System.Text.Json;
using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Exports the current server configuration as a GodMode manifest.
/// Reads profiles from appsettings.json and roots from disk.
/// </summary>
public class ManifestExporter : IManifestExporter
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManifestExporter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ManifestExporter(IConfiguration configuration, ILogger<ManifestExporter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public GodModeManifest Export()
    {
        var projectRootsDir = _configuration["ProjectRootsDir"];
        var profiles = _configuration.GetSection("Profiles").Get<Dictionary<string, ProfileConfig>>();

        // Build roots from ProjectRootsDir
        Dictionary<string, ManifestRoot>? roots = null;
        if (projectRootsDir != null)
        {
            var fullPath = Path.GetFullPath(projectRootsDir);
            if (Directory.Exists(fullPath))
            {
                roots = new Dictionary<string, ManifestRoot>();
                // Check installed.json for git sources
                var installedPath = Path.Combine(fullPath, "installed.json");
                var installed = new Dictionary<string, InstalledRootInfo>();
                if (File.Exists(installedPath))
                {
                    var json = File.ReadAllText(installedPath);
                    installed = JsonSerializer.Deserialize<Dictionary<string, InstalledRootInfo>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                }

                foreach (var dir in Directory.GetDirectories(fullPath))
                {
                    if (!Directory.Exists(Path.Combine(dir, ".godmode-root"))) continue;
                    var rootName = Path.GetFileName(dir)!;

                    if (installed.TryGetValue(rootName, out var info) && info.GitUrl != null)
                    {
                        roots[rootName] = new ManifestRoot(
                            Git: info.GitUrl,
                            Ref: info.GitRef,
                            GitPath: info.GitPath);
                    }
                    else
                    {
                        // Local root — use relative path
                        roots[rootName] = new ManifestRoot(Path: Path.Combine(projectRootsDir, rootName));
                    }
                }
            }
        }

        // Build profiles
        Dictionary<string, ManifestProfile>? manifestProfiles = null;
        if (profiles is { Count: > 0 })
        {
            manifestProfiles = new Dictionary<string, ManifestProfile>();
            foreach (var (name, config) in profiles)
            {
                manifestProfiles[name] = new ManifestProfile(
                    Roots: config.Roots.Count > 0 ? config.Roots.Keys.ToArray() : null,
                    Description: config.Description,
                    McpServers: config.McpServers,
                    Environment: config.Environment);
            }
        }

        // Settings
        var settings = new ManifestSettings(
            ProjectRootsDir: projectRootsDir,
            Urls: _configuration["Urls"]);

        _logger.LogInformation("Exported manifest: {RootCount} roots, {ProfileCount} profiles",
            roots?.Count ?? 0, manifestProfiles?.Count ?? 0);

        return new GodModeManifest(roots, manifestProfiles, settings);
    }

    public string Serialize(GodModeManifest manifest) =>
        JsonSerializer.Serialize(manifest, JsonOptions);
}
