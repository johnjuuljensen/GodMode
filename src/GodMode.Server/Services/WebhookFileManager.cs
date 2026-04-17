using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Manages webhook configuration as files on disk under {ProjectRootsDir}/.webhooks/.
/// Each webhook is a JSON file named {keyword}.json.
/// File-based: adding a webhook = adding a file. No shared file editing.
/// </summary>
public class WebhookFileManager
{
    private readonly string _webhooksDir;
    private readonly ILogger<WebhookFileManager> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public WebhookFileManager(IConfiguration configuration, ILogger<WebhookFileManager> logger)
    {
        var projectRootsDir = configuration["ProjectRootsDir"] ?? "roots";
        _webhooksDir = Path.Combine(Path.GetFullPath(projectRootsDir), ".webhooks");
        _logger = logger;
    }

    /// <summary>
    /// Full path to the .webhooks/ directory.
    /// </summary>
    public string WebhooksDir => _webhooksDir;

    /// <summary>
    /// Creates a new webhook with an auto-generated token.
    /// </summary>
    public WebhookConfig Create(string keyword, string profileName, string rootName,
        string? actionName = null, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null)
    {
        ValidateKeyword(keyword);
        var path = GetWebhookPath(keyword);
        if (File.Exists(path))
            throw new InvalidOperationException($"Webhook '{keyword}' already exists.");

        Directory.CreateDirectory(_webhooksDir);

        var token = GenerateToken();
        var config = new WebhookConfig(token, profileName, rootName, actionName, description, inputMapping, staticInputs);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));

        _logger.LogInformation("Created webhook '{Keyword}' → {Profile}/{Root}/{Action}",
            keyword, profileName, rootName, actionName ?? "(default)");
        return config;
    }

    /// <summary>
    /// Reads a single webhook config. Returns null if not found.
    /// </summary>
    public WebhookConfig? Read(string keyword)
    {
        var path = GetWebhookPath(keyword);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<WebhookConfig>(File.ReadAllText(path), JsonOptions);
    }

    /// <summary>
    /// Reads all webhooks. Returns keyword → config.
    /// </summary>
    public Dictionary<string, WebhookConfig> ReadAll()
    {
        var result = new Dictionary<string, WebhookConfig>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_webhooksDir)) return result;

        foreach (var file in Directory.GetFiles(_webhooksDir, "*.json"))
        {
            var keyword = Path.GetFileNameWithoutExtension(file)!;
            try
            {
                var config = JsonSerializer.Deserialize<WebhookConfig>(File.ReadAllText(file), JsonOptions);
                if (config != null)
                    result[keyword] = config;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read webhook config '{Keyword}'", keyword);
            }
        }

        return result;
    }

    /// <summary>
    /// Updates webhook settings. Preserves the existing token.
    /// </summary>
    public void Update(string keyword, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null,
        bool? enabled = null)
    {
        var existing = Read(keyword)
            ?? throw new KeyNotFoundException($"Webhook '{keyword}' not found.");

        var updated = existing with
        {
            Description = description ?? existing.Description,
            InputMapping = inputMapping ?? existing.InputMapping,
            StaticInputs = staticInputs ?? existing.StaticInputs,
            Enabled = enabled ?? existing.Enabled
        };

        File.WriteAllText(GetWebhookPath(keyword), JsonSerializer.Serialize(updated, JsonOptions));
        _logger.LogInformation("Updated webhook '{Keyword}'", keyword);
    }

    /// <summary>
    /// Deletes a webhook.
    /// </summary>
    public void Delete(string keyword)
    {
        var path = GetWebhookPath(keyword);
        if (File.Exists(path))
            File.Delete(path);

        _logger.LogInformation("Deleted webhook '{Keyword}'", keyword);
    }

    /// <summary>
    /// Regenerates the token for a webhook. Returns the new full token.
    /// </summary>
    public string RegenerateToken(string keyword)
    {
        var existing = Read(keyword)
            ?? throw new KeyNotFoundException($"Webhook '{keyword}' not found.");

        var newToken = GenerateToken();
        var updated = existing with { Token = newToken };
        File.WriteAllText(GetWebhookPath(keyword), JsonSerializer.Serialize(updated, JsonOptions));

        _logger.LogInformation("Regenerated token for webhook '{Keyword}'", keyword);
        return newToken;
    }

    /// <summary>
    /// Validates a token against the stored token using constant-time comparison.
    /// </summary>
    public bool ValidateToken(string keyword, string token)
    {
        var config = Read(keyword);
        if (config == null) return false;

        var storedBytes = Encoding.UTF8.GetBytes(config.Token);
        var providedBytes = Encoding.UTF8.GetBytes(token);

        return CryptographicOperations.FixedTimeEquals(storedBytes, providedBytes);
    }

    /// <summary>
    /// Converts a WebhookConfig to a WebhookInfo (token redacted).
    /// </summary>
    public static WebhookInfo ToInfo(string keyword, WebhookConfig config) =>
        new(keyword, config.ProfileName, config.RootName, config.ActionName,
            config.Description, config.Enabled,
            config.Token.Length >= 8 ? config.Token[..8] + "..." : "***");

    private string GetWebhookPath(string keyword) => Path.Combine(_webhooksDir, $"{keyword}.json");

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "whk_" + Convert.ToHexStringLower(bytes);
    }

    private static void ValidateKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Webhook keyword cannot be empty.");

        if (keyword.Length > 64)
            throw new ArgumentException("Webhook keyword must be 64 characters or fewer.");

        // Only allow alphanumeric, hyphens, and underscores — no path traversal
        foreach (var c in keyword)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                throw new ArgumentException($"Webhook keyword contains invalid character: '{c}'. Only letters, digits, hyphens, and underscores are allowed.");
        }
    }
}
