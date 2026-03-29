using System.Text.Json;

namespace GodMode.Server.Auth;

/// <summary>
/// Manages the allowed email for Google OAuth.
/// Reads from GODMODE_ALLOWED_EMAIL env var, falls back to ~/.godmode/auth.json.
/// </summary>
public class GoogleAuthConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".godmode", "auth.json");

    private string? _cachedEmail;

    public string? GetAllowedEmail()
    {
        if (_cachedEmail != null) return _cachedEmail;

        // Check env var first
        var envEmail = Environment.GetEnvironmentVariable("GODMODE_ALLOWED_EMAIL");
        if (!string.IsNullOrWhiteSpace(envEmail))
        {
            _cachedEmail = envEmail.Trim().ToLowerInvariant();
            return _cachedEmail;
        }

        // Fall back to config file
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(ConfigPath));
                if (json.TryGetProperty("allowedEmail", out var emailProp))
                {
                    var email = emailProp.GetString();
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        _cachedEmail = email.Trim().ToLowerInvariant();
                        return _cachedEmail;
                    }
                }
            }
            catch { /* ignore corrupt file */ }
        }

        return null;
    }

    public void SetAllowedEmail(string email)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var normalized = email.Trim().ToLowerInvariant();
        var json = JsonSerializer.Serialize(new { allowedEmail = normalized },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
        _cachedEmail = normalized;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(GetAllowedEmail());

    public bool IsGoogleOAuthConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"));
}
