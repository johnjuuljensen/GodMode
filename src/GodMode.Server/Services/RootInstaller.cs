using System.Diagnostics;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Installs shared roots from packages, URLs, or git repositories into ProjectRootsDir.
/// Tracks installed roots in installed.json for update detection.
/// </summary>
public class RootInstaller
{
    private readonly RootPackager _packager;
    private readonly RootCreator _creator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RootInstaller> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public RootInstaller(
        RootPackager packager,
        RootCreator creator,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RootInstaller> logger)
    {
        _packager = packager;
        _creator = creator;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Installs a root from a SharedRootPreview (already extracted/reviewed by user).
    /// </summary>
    public void Install(SharedRootPreview preview, string? localName = null)
    {
        var rootsDir = GetRootsDir();
        var name = localName ?? preview.Manifest.Name;
        var rootPath = Path.Combine(rootsDir, name);

        if (Directory.Exists(Path.Combine(rootPath, ".godmode-root")))
            throw new InvalidOperationException($"Root '{name}' already exists. Uninstall first or choose a different name.");

        var rootPreview = new RootPreview(preview.Files);
        var validation = _creator.Validate(rootPreview);
        if (validation != null)
            throw new InvalidOperationException($"Package validation failed: {validation}");

        Directory.CreateDirectory(rootPath);
        _creator.WriteRoot(rootPath, rootPreview);

        var installed = new InstalledRootInfo(
            RootName: name,
            Source: preview.Manifest.Source ?? "file",
            Version: preview.Manifest.Version,
            InstalledAt: DateTime.UtcNow,
            ScriptHashes: preview.ScriptHashes
        );
        SaveInstalledInfo(name, installed);

        _logger.LogInformation("Installed shared root '{Name}' from {Source}", name, installed.Source);
    }

    /// <summary>
    /// Previews a root from a URL (downloads .gmroot package).
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromUrlAsync(string url, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient();
        return await _packager.ExtractFromUrlAsync(url, http, ct);
    }

    /// <summary>
    /// Previews a root from raw package bytes (file upload).
    /// </summary>
    public SharedRootPreview PreviewFromBytes(byte[] packageBytes) =>
        _packager.Extract(packageBytes);

    /// <summary>
    /// Previews a root from a git repository.
    /// Clones (shallow) to temp dir, looks for .godmode-root/, extracts.
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromGitAsync(
        string repoUrl, string? subPath = null, string? gitRef = null, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"godmode-git-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var refArg = gitRef != null ? $"--branch {gitRef}" : "";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --depth 1 {refArg} {repoUrl} .",
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"git clone failed: {stderr}");
            }

            var searchPath = subPath != null ? Path.Combine(tempDir, subPath) : tempDir;
            var godmodeRoot = Path.Combine(searchPath, ".godmode-root");

            if (!Directory.Exists(godmodeRoot))
                throw new InvalidOperationException($"No .godmode-root/ directory found in {subPath ?? "repository root"}");

            var preview = _creator.ReadExistingRoot(searchPath);

            // Get commit SHA
            string? commitSha = null;
            try
            {
                var shaProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse HEAD",
                        WorkingDirectory = tempDir,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                shaProcess.Start();
                commitSha = (await shaProcess.StandardOutput.ReadToEndAsync(ct)).Trim();
                await shaProcess.WaitForExitAsync(ct);
            }
            catch { /* ignore */ }

            var manifest = new RootManifest(
                Name: Path.GetFileName(searchPath),
                DisplayName: Path.GetFileName(searchPath),
                Source: repoUrl + (subPath != null ? $"/{subPath}" : "") + (gitRef != null ? $"@{gitRef}" : "")
            );

            var scriptHashes = new Dictionary<string, string>();
            foreach (var (path, content) in preview.Files)
            {
                if (IsScriptFile(path))
                    scriptHashes[path] = RootPackager.ComputeHash(content);
            }

            return new SharedRootPreview(manifest, preview.Files, scriptHashes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Removes an installed shared root.
    /// </summary>
    public void Uninstall(string rootName)
    {
        var rootsDir = GetRootsDir();
        var rootPath = Path.Combine(rootsDir, rootName);

        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);

        RemoveInstalledInfo(rootName);
        _logger.LogInformation("Uninstalled shared root '{Name}'", rootName);
    }

    /// <summary>
    /// Gets all installed root tracking info.
    /// </summary>
    public Dictionary<string, InstalledRootInfo> GetInstalledRoots()
    {
        var path = GetInstalledJsonPath();
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, InstalledRootInfo>>(json, JsonOptions) ?? new();
    }

    private void SaveInstalledInfo(string name, InstalledRootInfo info)
    {
        var installed = GetInstalledRoots();
        installed[name] = info;
        var path = GetInstalledJsonPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private void RemoveInstalledInfo(string name)
    {
        var installed = GetInstalledRoots();
        installed.Remove(name);
        var path = GetInstalledJsonPath();
        File.WriteAllText(path, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private string GetRootsDir() =>
        _configuration["ProjectRootsDir"]
        ?? throw new InvalidOperationException("ProjectRootsDir not configured");

    private string GetInstalledJsonPath() =>
        Path.Combine(GetRootsDir(), "installed.json");

    private static bool IsScriptFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".sh" or ".ps1" or ".cmd" or ".bat";
    }
}
