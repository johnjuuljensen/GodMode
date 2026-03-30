using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Compares manifest to disk state and reconciles.
/// Three-phase: roots, profiles, MCP servers.
/// </summary>
public class ConvergenceEngine : IConvergenceEngine
{
    private readonly ConfigFileWriter _configFileWriter;
    private readonly RootCreator _rootCreator;
    private readonly IManifestParser _manifestParser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConvergenceEngine> _logger;

    public ConvergenceEngine(
        ConfigFileWriter configFileWriter,
        RootCreator rootCreator,
        IManifestParser manifestParser,
        IConfiguration configuration,
        ILogger<ConvergenceEngine> logger)
    {
        _configFileWriter = configFileWriter;
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

        // Phase 2: Profiles
        if (manifest.Profiles != null)
        {
            foreach (var (profileName, profileDef) in manifest.Profiles)
            {
                try
                {
                    _configFileWriter.CreateProfile(profileName, profileDef.Description);
                    actions.Add($"Created/updated profile '{profileName}'");
                }
                catch (InvalidOperationException)
                {
                    // Profile already exists — update description if needed
                    if (profileDef.Description != null)
                    {
                        try
                        {
                            _configFileWriter.UpdateProfileDescription(profileName, profileDef.Description);
                        }
                        catch { /* already up to date */ }
                    }
                }
            }
        }

        // Phase 3: MCP servers (profile-level)
        if (manifest.Profiles != null)
        {
            foreach (var (profileName, profileDef) in manifest.Profiles)
            {
                if (profileDef.McpServers == null) continue;
                foreach (var (serverName, config) in profileDef.McpServers)
                {
                    try
                    {
                        _configFileWriter.AddMcpServerToProfile(profileName, serverName, config);
                        actions.Add($"Added MCP server '{serverName}' to profile '{profileName}'");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"MCP server '{serverName}' in profile '{profileName}': {ex.Message}");
                    }
                }
            }
        }

        _logger.LogInformation("Convergence complete: {ActionCount} actions, {ErrorCount} errors, {WarningCount} warnings",
            actions.Count, errors.Count, warnings.Count);

        return new ConvergenceResult(actions, errors, warnings);
    }
}
