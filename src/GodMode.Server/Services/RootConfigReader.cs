using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads .godmode-root.json configuration from project root directories.
/// No caching — always reads fresh so changes take effect without restart.
/// </summary>
public class RootConfigReader : IRootConfigReader
{
    private const string ConfigFileName = ".godmode-root.json";

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
        var configPath = Path.Combine(rootPath, ConfigFileName);

        if (!File.Exists(configPath))
        {
            _logger.LogDebug("No {ConfigFile} found in {RootPath}, using default config", ConfigFileName, rootPath);
            return new RootConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<RootConfig>(json, JsonOptions);
            return config ?? new RootConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {ConfigFile} from {RootPath}, using default config", ConfigFileName, rootPath);
            return new RootConfig();
        }
    }
}
