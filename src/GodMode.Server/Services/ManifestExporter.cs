using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Exports the current server configuration as a GodMode manifest.
/// Reads profiles from .profiles/ directory and roots from disk.
/// </summary>
public class ManifestExporter : IManifestExporter
{
    private readonly ProfileFileManager _profileFileManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManifestExporter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ManifestExporter(
        ProfileFileManager profileFileManager,
        IConfiguration configuration,
        ILogger<ManifestExporter> logger)
    {
        _profileFileManager = profileFileManager;
        _configuration = configuration;
        _logger = logger;
    }

    public GodModeManifest Export()
    {
        var projectRootsDir = _configuration["ProjectRootsDir"];

        // Build roots from ProjectRootsDir
        Dictionary<string, ManifestRoot>? roots = null;
        if (projectRootsDir != null)
        {
            var fullPath = Path.GetFullPath(projectRootsDir);
            if (Directory.Exists(fullPath))
            {
                roots = new Dictionary<string, ManifestRoot>();

                foreach (var dir in Directory.GetDirectories(fullPath))
                {
                    if (!Directory.Exists(Path.Combine(dir, ".godmode-root"))) continue;
                    var rootName = Path.GetFileName(dir)!;

                    // Read per-root source.json for provenance (self-describing)
                    var source = RootInstaller.ReadSourceJson(dir);
                    if (source?.Git != null)
                    {
                        roots[rootName] = new ManifestRoot(
                            Git: source.Git,
                            Ref: source.Ref,
                            GitPath: source.Path);
                    }
                    else
                    {
                        // Local root — use relative path
                        roots[rootName] = new ManifestRoot(Path: Path.Combine(projectRootsDir, rootName));
                    }
                }
            }
        }

        // Build profiles from .profiles/ directory (self-describing)
        Dictionary<string, ManifestProfile>? manifestProfiles = null;
        var fileProfiles = _profileFileManager.ReadAllProfiles();
        if (fileProfiles.Count > 0)
        {
            manifestProfiles = new Dictionary<string, ManifestProfile>();
            foreach (var (name, data) in fileProfiles)
            {
                manifestProfiles[name] = new ManifestProfile(
                    Description: data.Description,
                    McpServers: data.McpServers,
                    Environment: data.Environment);
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
