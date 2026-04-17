using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Compares manifest to disk state and reconciles.
/// Three-phase: roots, profiles, MCP servers.
/// </summary>
public class ConvergenceEngine : IConvergenceEngine
{
    private readonly ProfileFileManager _profileFileManager;
    private readonly RootCreator _rootCreator;
    private readonly IManifestParser _manifestParser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConvergenceEngine> _logger;

    public ConvergenceEngine(
        ProfileFileManager profileFileManager,
        RootCreator rootCreator,
        IManifestParser manifestParser,
        IConfiguration configuration,
        ILogger<ConvergenceEngine> logger)
    {
        _profileFileManager = profileFileManager;
        _rootCreator = rootCreator;
        _manifestParser = manifestParser;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ConvergenceResult> ConvergeAsync(GodModeManifest manifest, bool force = false)
    {
        var validation = _manifestParser.Validate(manifest);
        if (validation != null)
            return new ConvergenceResult([], [$"Manifest validation failed: {validation}"], []);

        var actions = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();

        var projectRootsDir = manifest.Settings?.ProjectRootsDir
            ?? _configuration["ProjectRootsDir"]
            ?? "roots";

        var fullRootsDir = Path.GetFullPath(projectRootsDir);
        Directory.CreateDirectory(fullRootsDir);

        // Phase 1: Roots
        if (manifest.Roots != null)
        {
            var existingRootDirs = Directory.Exists(fullRootsDir)
                ? Directory.GetDirectories(fullRootsDir)
                    .Where(d => Directory.Exists(Path.Combine(d, ".godmode-root")))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (rootName, rootDef) in manifest.Roots)
            {
                var rootPath = Path.Combine(fullRootsDir, rootName);
                try
                {
                    if (rootDef.Git != null)
                    {
                        // Git source — clone to a temp location, copy .godmode-root/ into rootPath
                        var tempDir = Path.Combine(Path.GetTempPath(), $"godmode-converge-{Guid.NewGuid():N}");
                        try
                        {
                            await GitFetcher.CloneOrPullAsync(rootDef.Git, tempDir, rootDef.Ref);
                            var sourcePath = rootDef.GitPath != null
                                ? Path.Combine(tempDir, rootDef.GitPath)
                                : tempDir;

                            var preview = _rootCreator.ReadExistingRoot(sourcePath);
                            if (preview == null)
                            {
                                errors.Add($"Root '{rootName}': no .godmode-root/ in git source.");
                                continue;
                            }

                            // Clear and rewrite
                            var godModeRootPath = Path.Combine(rootPath, ".godmode-root");
                            if (Directory.Exists(godModeRootPath))
                                Directory.Delete(godModeRootPath, recursive: true);

                            Directory.CreateDirectory(rootPath);
                            _rootCreator.WriteRoot(rootPath, preview);

                            // Write source.json so the root is self-describing
                            RootInstaller.WriteSourceJson(rootPath, new RootSourceInfo(
                                Git: rootDef.Git, Ref: rootDef.Ref, Path: rootDef.GitPath,
                                InstalledAt: DateTime.UtcNow));

                            actions.Add($"Synced root '{rootName}' from {rootDef.Git}");
                        }
                        finally
                        {
                            if (Directory.Exists(tempDir))
                                Directory.Delete(tempDir, recursive: true);
                        }
                    }
                    else if (rootDef.Path != null)
                    {
                        // Local path — verify it exists
                        if (!Directory.Exists(Path.Combine(rootDef.Path, ".godmode-root")))
                        {
                            // If it's the same as rootPath, nothing to do (it's already in the roots dir)
                            if (Path.GetFullPath(rootDef.Path) != Path.GetFullPath(rootPath))
                            {
                                // Copy .godmode-root/ from source to destination
                                var preview = _rootCreator.ReadExistingRoot(rootDef.Path);
                                if (preview != null)
                                {
                                    Directory.CreateDirectory(rootPath);
                                    _rootCreator.WriteRoot(rootPath, preview);
                                    actions.Add($"Copied root '{rootName}' from {rootDef.Path}");
                                }
                                else
                                {
                                    warnings.Add($"Root '{rootName}': source path '{rootDef.Path}' has no .godmode-root/.");
                                }
                            }
                        }
                    }

                    existingRootDirs.Remove(rootName);
                }
                catch (Exception ex)
                {
                    errors.Add($"Root '{rootName}': {ex.Message}");
                }
            }

            // Remove undeclared roots
            foreach (var orphan in existingRootDirs)
            {
                if (!force)
                {
                    warnings.Add($"Root '{orphan}' not in manifest — skipped (use force to remove).");
                    continue;
                }

                var orphanPath = Path.Combine(fullRootsDir, orphan, ".godmode-root");
                if (Directory.Exists(orphanPath))
                {
                    Directory.Delete(orphanPath, recursive: true);
                    actions.Add($"Removed undeclared root '{orphan}'");
                }
            }
        }

        // Phase 2: Profiles — copy profile directories from manifest sources into .profiles/
        if (manifest.Profiles != null)
        {
            foreach (var (profileName, profileDef) in manifest.Profiles)
            {
                try
                {
                    if (profileDef.Git != null)
                    {
                        // Git source — clone and copy profile directory
                        var tempDir = Path.Combine(Path.GetTempPath(), $"godmode-profile-{Guid.NewGuid():N}");
                        try
                        {
                            await GitFetcher.CloneOrPullAsync(profileDef.Git, tempDir, profileDef.Ref);
                            var sourcePath = profileDef.GitPath != null
                                ? Path.Combine(tempDir, profileDef.GitPath)
                                : tempDir;

                            CopyProfileDirectory(sourcePath, Path.Combine(_profileFileManager.ProfilesDir, profileName));
                            actions.Add($"Synced profile '{profileName}' from {profileDef.Git}");
                        }
                        finally
                        {
                            if (Directory.Exists(tempDir))
                                Directory.Delete(tempDir, recursive: true);
                        }
                    }
                    else if (profileDef.Path != null)
                    {
                        // Local path — copy profile directory
                        var targetDir = Path.Combine(_profileFileManager.ProfilesDir, profileName);
                        if (Path.GetFullPath(profileDef.Path) != Path.GetFullPath(targetDir))
                        {
                            CopyProfileDirectory(profileDef.Path, targetDir);
                            actions.Add($"Copied profile '{profileName}' from {profileDef.Path}");
                        }
                    }
                    else
                    {
                        // Inline profile definition (description, MCP servers, environment in manifest)
                        _profileFileManager.CreateProfile(profileName, profileDef.Description);
                        if (profileDef.McpServers != null)
                        {
                            foreach (var (serverName, config) in profileDef.McpServers)
                                _profileFileManager.AddMcpServerToProfile(profileName, serverName, config);
                        }
                        if (profileDef.Environment is { Count: > 0 })
                            _profileFileManager.SetProfileEnvironment(profileName, profileDef.Environment);

                        actions.Add($"Created profile '{profileName}' from manifest");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Profile '{profileName}': {ex.Message}");
                }
            }
        }

        _logger.LogInformation("Convergence complete: {ActionCount} actions, {ErrorCount} errors, {WarningCount} warnings",
            actions.Count, errors.Count, warnings.Count);

        return new ConvergenceResult(actions, errors, warnings);
    }

    private static void CopyProfileDirectory(string source, string target)
    {
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(target, relativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir != null) Directory.CreateDirectory(targetDir);
            File.Copy(file, targetPath, overwrite: true);
        }
    }
}
