namespace GodMode.Server.Auth;

/// <summary>
/// How callers of the server are authenticated. Exactly one mode is active per run, and every mode
/// needs a credential, whatever the server is bound to.
/// </summary>
public enum AuthMode
{
    /// <summary>Bearer token must equal the server's API key: <c>Authentication:ApiKey</c>, else the one in its key file.</summary>
    ApiKey,

    /// <summary>
    /// GitHub Codespace: bearer token must be a GitHub token owned by <c>GITHUB_USER</c>, other than the
    /// codespace's own <c>GITHUB_TOKEN</c>, which its sessions are given.
    /// </summary>
    Codespace,
}

/// <summary>Auth settings resolved once at startup and shared with the authentication handler.</summary>
/// <param name="ApiKey">The key callers present in <see cref="AuthMode.ApiKey"/> mode.</param>
/// <param name="GitHubUser">Who must own the token in <see cref="AuthMode.Codespace"/> mode.</param>
/// <param name="CodespaceToken">The codespace's own <c>GITHUB_TOKEN</c>, refused in <see cref="AuthMode.Codespace"/> mode.</param>
/// <param name="KeyFilePath">Where <see cref="ApiKey"/> is kept when none is configured.</param>
/// <param name="KeyFileCreated">This start generated the key and created <see cref="KeyFilePath"/>.</param>
public sealed record AuthSettings(
    AuthMode Mode,
    string? ApiKey = null,
    string? GitHubUser = null,
    string? CodespaceToken = null,
    string? KeyFilePath = null,
    bool KeyFileCreated = false)
{
    /// <summary>Names no secret, so the settings can be logged.</summary>
    public override string ToString() => $"{Mode}{(KeyFilePath != null ? $" (key file {KeyFilePath})" : "")}";
}

public static class AuthModeSelector
{
    public const string ApiKeySetting = "Authentication:ApiKey";

    /// <summary>
    /// A codespace (<c>CODESPACES=true</c>) authenticates GitHub tokens, whatever else is set. Anywhere
    /// else it is the API key: the configured one, else the key file's, which the first start
    /// generates (<see cref="ApiKeyFile"/>). Throws <see cref="StartupConfigurationException"/> when
    /// the key file cannot be used.
    /// </summary>
    public static AuthSettings Resolve(IConfiguration config)
    {
        if (string.Equals(config["CODESPACES"], "true", StringComparison.OrdinalIgnoreCase))
            return new AuthSettings(AuthMode.Codespace, GitHubUser: config["GITHUB_USER"], CodespaceToken: config["GITHUB_TOKEN"]);

        if (config[ApiKeySetting] is { } configured && !string.IsNullOrWhiteSpace(configured))
            return new AuthSettings(AuthMode.ApiKey, configured);

        var path = ApiKeyFile.PathFrom(config);
        var (key, created) = ApiKeyFile.LoadOrCreate(path);
        return new AuthSettings(AuthMode.ApiKey, key, KeyFilePath: path, KeyFileCreated: created);
    }
}
