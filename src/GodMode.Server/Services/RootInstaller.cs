using System.Diagnostics;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Installs shared roots from various sources (bytes, URL, git repo).
/// Provenance is stored per-root in .godmode-root/source.json (self-describing, no centralized tracking).
/// </summary>
public class RootInstaller
{
    private readonly RootCreator _rootCreator;
    private readonly RootPackager _rootPackager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RootInstaller> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
    /// Writes source.json into .godmode-root/ for self-describing provenance.
    /// </summary>
    public void Install(string projectRootsDir, string rootName, SharedRootPreview preview)
    {
        var rootPath = Path.Combine(Path.GetFullPath(projectRootsDir), rootName);
        Directory.CreateDirectory(rootPath);
        _rootCreator.WriteRoot(rootPath, preview.Preview);

        // Write provenance as source.json inside .godmode-root/
        var sourceInfo = ParseSourceInfo(preview.Source, preview.Manifest.Version);
        if (sourceInfo != null)
            WriteSourceJson(rootPath, sourceInfo);

        _logger.LogInformation("Installed shared root '{RootName}' from {Source}", rootName, preview.Source);
    }

    /// <summary>
    /// Uninstalls a shared root (removes directory). source.json is removed with the directory.
    /// </summary>
    public void Uninstall(string projectRootsDir, string rootName)
    {
        var rootPath = Path.Combine(Path.GetFullPath(projectRootsDir), rootName);
        var godModeRootPath = Path.Combine(rootPath, ".godmode-root");
        if (Directory.Exists(godModeRootPath))
            Directory.Delete(godModeRootPath, recursive: true);

        if (Directory.Exists(rootPath) && !Directory.EnumerateFileSystemEntries(rootPath).Any())
            Directory.Delete(rootPath);

        _logger.LogInformation("Uninstalled shared root '{RootName}'", rootName);
    }

    /// <summary>
    /// Writes source.json for a root (used by both Install and ConvergenceEngine).
    /// </summary>
    public static void WriteSourceJson(string rootPath, RootSourceInfo sourceInfo)
    {
        var sourcePath = Path.Combine(rootPath, ".godmode-root", "source.json");
        var dir = Path.GetDirectoryName(sourcePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(sourcePath, JsonSerializer.Serialize(sourceInfo, JsonOptions));
    }

    /// <summary>
    /// Reads source.json from a root, or null if not present (local root).
    /// </summary>
    public static RootSourceInfo? ReadSourceJson(string rootPath)
    {
        var sourcePath = Path.Combine(rootPath, ".godmode-root", "source.json");
        if (!File.Exists(sourcePath)) return null;
        var json = File.ReadAllText(sourcePath);
        return JsonSerializer.Deserialize<RootSourceInfo>(json, JsonOptions);
    }

    private static RootSourceInfo? ParseSourceInfo(string? source, string? version)
    {
        if (string.IsNullOrEmpty(source)) return null;

        // Parse "git:{url}#{ref}:{path}" format
        if (source.StartsWith("git:", StringComparison.Ordinal))
        {
            var rest = source["git:".Length..];
            string? gitUrl = rest, gitRef = null, gitPath = null;

            var hashIdx = rest.IndexOf('#');
            if (hashIdx >= 0)
            {
                gitUrl = rest[..hashIdx];
                var afterHash = rest[(hashIdx + 1)..];
                var colonIdx = afterHash.IndexOf(':');
                if (colonIdx >= 0)
                {
                    gitRef = afterHash[..colonIdx];
                    gitPath = afterHash[(colonIdx + 1)..];
                    if (gitPath == "/") gitPath = null;
                }
                else
                {
                    gitRef = afterHash;
                }
            }

            return new RootSourceInfo(
                Git: gitUrl,
                Ref: gitRef == "HEAD" ? null : gitRef,
                Path: gitPath,
                InstalledAt: DateTime.UtcNow,
                Version: version);
        }

        // URL source — store the URL in Git field for simplicity (it's still a remote source)
        if (source.StartsWith("url:", StringComparison.Ordinal))
        {
            return new RootSourceInfo(
                Git: source["url:".Length..],
                InstalledAt: DateTime.UtcNow,
                Version: version);
        }

        return null;
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
}
