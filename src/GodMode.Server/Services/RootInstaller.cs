using System.Diagnostics;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Installs shared roots from various sources (bytes, URL, git repo).
/// Tracks installed roots via installed.json for update detection.
/// </summary>
public class RootInstaller
{
    private readonly RootCreator _rootCreator;
    private readonly RootPackager _rootPackager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RootInstaller> _logger;

    public RootInstaller(
        RootCreator rootCreator,
        RootPackager rootPackager,
        IHttpClientFactory httpClientFactory,
        ILogger<RootInstaller> logger)
    {
        _rootCreator = rootCreator;
        _rootPackager = rootPackager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Previews a .gmroot package from a URL before installation.
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromUrlAsync(string url)
    {
        var client = _httpClientFactory.CreateClient();
        var bytes = await client.GetByteArrayAsync(url);
        return RootPackager.PreviewFromBytes(bytes, $"url:{url}");
    }

    /// <summary>
    /// Previews a root from a git repo (shallow clone, extract .godmode-root/).
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromGitAsync(string gitUrl, string? path = null, string? gitRef = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"godmode-import-{Guid.NewGuid():N}");
        try
        {
            await ShallowCloneAsync(gitUrl, tempDir, gitRef);

            var rootSourceDir = path != null
                ? Path.Combine(tempDir, path)
                : tempDir;

            var preview = _rootCreator.ReadExistingRoot(rootSourceDir)
                ?? throw new InvalidOperationException($"No .godmode-root/ found at path '{path ?? "/"}' in repo.");

            var manifest = new RootManifest(
                Name: Path.GetFileName(path ?? new Uri(gitUrl).AbsolutePath.TrimEnd('/').Split('/').Last()),
                Description: null,
                ExportedAt: DateTime.UtcNow);

            return new SharedRootPreview(manifest, preview, $"git:{gitUrl}#{gitRef ?? "HEAD"}:{path ?? "/"}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Installs a previewed shared root into ProjectRootsDir.
    /// </summary>
    public void Install(string projectRootsDir, string rootName, SharedRootPreview preview)
    {
        var rootPath = Path.Combine(Path.GetFullPath(projectRootsDir), rootName);
        Directory.CreateDirectory(rootPath);
        _rootCreator.WriteRoot(rootPath, preview.Preview);

        // Track installation info
        SaveInstalledInfo(projectRootsDir, new InstalledRootInfo(
            RootName: rootName,
            Source: preview.Source ?? "unknown",
            InstalledAt: DateTime.UtcNow,
            ManifestVersion: preview.Manifest.Version));

        _logger.LogInformation("Installed shared root '{RootName}' from {Source}", rootName, preview.Source);
    }

    /// <summary>
    /// Uninstalls a shared root (removes directory and tracking info).
    /// </summary>
    public void Uninstall(string projectRootsDir, string rootName)
    {
        var rootPath = Path.Combine(Path.GetFullPath(projectRootsDir), rootName);
        var godModeRootPath = Path.Combine(rootPath, ".godmode-root");
        if (Directory.Exists(godModeRootPath))
            Directory.Delete(godModeRootPath, recursive: true);

        if (Directory.Exists(rootPath) && !Directory.EnumerateFileSystemEntries(rootPath).Any())
            Directory.Delete(rootPath);

        RemoveInstalledInfo(projectRootsDir, rootName);
        _logger.LogInformation("Uninstalled shared root '{RootName}'", rootName);
    }

    private static async Task ShallowCloneAsync(string gitUrl, string targetDir, string? gitRef)
    {
        var args = gitRef != null
            ? $"clone --depth 1 --branch {gitRef} {gitUrl} {targetDir}"
            : $"clone --depth 1 {gitUrl} {targetDir}";

        var psi = new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git.");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git clone failed: {stderr}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void SaveInstalledInfo(string projectRootsDir, InstalledRootInfo info)
    {
        var installedPath = Path.Combine(Path.GetFullPath(projectRootsDir), "installed.json");
        var installed = LoadInstalled(installedPath);
        installed[info.RootName] = info;
        File.WriteAllText(installedPath, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private static void RemoveInstalledInfo(string projectRootsDir, string rootName)
    {
        var installedPath = Path.Combine(Path.GetFullPath(projectRootsDir), "installed.json");
        var installed = LoadInstalled(installedPath);
        installed.Remove(rootName);
        File.WriteAllText(installedPath, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private static Dictionary<string, InstalledRootInfo> LoadInstalled(string path)
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, InstalledRootInfo>>(json, JsonOptions) ?? new();
    }
}
