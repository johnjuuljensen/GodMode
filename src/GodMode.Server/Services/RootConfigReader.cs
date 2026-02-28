using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads .godmode-root/config.json configuration from project root directories.
/// Falls back to legacy .godmode-root.json if the new path doesn't exist.
/// No caching — always reads fresh so changes take effect without restart.
/// </summary>
public class RootConfigReader : IRootConfigReader
{
    private const string ConfigPath = ".godmode-root/config.json";
    private const string LegacyConfigFileName = ".godmode-root.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ILogger<RootConfigReader> _logger;

    public RootConfigReader(ILogger<RootConfigReader> logger)
    {
        _logger = logger;
    }

    public RootConfig ReadConfig(string rootPath)
    {
        var configPath = Path.Combine(rootPath, ConfigPath);

        // Fall back to legacy path for backward compatibility
        if (!File.Exists(configPath))
        {
            var legacyPath = Path.Combine(rootPath, LegacyConfigFileName);
            if (File.Exists(legacyPath))
            {
                _logger.LogDebug("Using legacy {LegacyFile} in {RootPath} (migrate to {NewPath})",
                    LegacyConfigFileName, rootPath, ConfigPath);
                configPath = legacyPath;
            }
            else
            {
                _logger.LogDebug("No config found in {RootPath}, using default config", rootPath);
                return new RootConfig();
            }
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<RootConfig>(json, JsonOptions);
            return config ?? new RootConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read config from {ConfigPath}, using default config", configPath);
            return new RootConfig();
        }
    }
}
