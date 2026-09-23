using System.Net;

namespace GodMode.Server.Auth;

/// <summary>
/// How callers of the server are authenticated. Exactly one mode is active per run.
/// </summary>
public enum AuthMode
{
    /// <summary>Bearer token must equal <c>Authentication:ApiKey</c>.</summary>
    ApiKey,

    /// <summary>GitHub Codespace: bearer token must be a GitHub token owned by <c>GITHUB_USER</c>.</summary>
    Codespace,

    /// <summary>No key configured and bound to loopback only: loopback callers are trusted.</summary>
    Loopback,
}

/// <summary>Auth settings resolved once at startup and shared with the authentication handler.</summary>
public sealed record AuthSettings(AuthMode Mode, string? ApiKey, string? GitHubUser);

/// <summary>The configuration would expose the server without authentication.</summary>
public sealed class AuthConfigurationException(string message) : Exception(message);

public static class AuthModeSelector
{
    public const string ApiKeySetting = "Authentication:ApiKey";

    /// <summary>Kestrel's binding when no URL is configured anywhere.</summary>
    private const string KestrelDefaultUrl = "http://localhost:5000";

    /// <summary>
    /// Codespace wins, then an API key. With neither, the server may only run unauthenticated
    /// when every binding is loopback; anything else throws <see cref="AuthConfigurationException"/>.
    /// </summary>
    public static AuthMode Select(string? apiKey, bool isCodespace, IReadOnlyCollection<string> urls) =>
        isCodespace ? AuthMode.Codespace
        : !string.IsNullOrEmpty(apiKey) ? AuthMode.ApiKey
        : urls.All(IsLoopbackUrl) ? AuthMode.Loopback
        : throw new AuthConfigurationException(
            $"GodMode.Server will not start: no API key is configured and it is bound to " +
            $"{string.Join(", ", urls.Where(u => !IsLoopbackUrl(u)))}, which is not loopback. " +
            $"Anyone who can reach that address could run Claude Code on this machine.{Environment.NewLine}" +
            $"Either set an API key ({ApiKeySetting} in appsettings.json, the Authentication__ApiKey " +
            $"environment variable, or --{ApiKeySetting}=<key>), or bind to loopback only " +
            $"(--urls http://127.0.0.1:31337).");

    public static AuthSettings Resolve(IConfiguration config)
    {
        var apiKey = config[ApiKeySetting];
        var isCodespace = string.Equals(config["CODESPACES"], "true", StringComparison.OrdinalIgnoreCase);
        var mode = Select(apiKey, isCodespace, GetConfiguredUrls(config));
        return new AuthSettings(mode, mode == AuthMode.ApiKey ? apiKey : null, config["GITHUB_USER"]);
    }

    /// <summary>
    /// Every URL Kestrel will bind, from the same sources Kestrel reads: <c>Urls</c>
    /// (appsettings, <c>--urls</c>, <c>URLS</c>/<c>ASPNETCORE_URLS</c>), else <c>HTTP_PORTS</c>/<c>HTTPS_PORTS</c>
    /// (all interfaces), plus <c>Kestrel:Endpoints:*:Url</c>. Falls back to Kestrel's own default.
    /// </summary>
    public static IReadOnlyList<string> GetConfiguredUrls(IConfiguration config)
    {
        static IEnumerable<string> Split(string? value) =>
            (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Kestrel uses HTTP(S)_PORTS only when no Urls are set (the aspnet base image sets HTTP_PORTS=8080)
        var urls = Split(config["urls"]).ToList();
        if (urls.Count == 0)
            urls.AddRange(Split(config["http_ports"]).Select(port => $"http://*:{port}")
                .Concat(Split(config["https_ports"]).Select(port => $"https://*:{port}")));

        urls.AddRange(config.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"])
            .OfType<string>());

        return urls.Count > 0 ? urls : [KestrelDefaultUrl];
    }

    /// <summary>True only for <c>localhost</c> and loopback IP literals. Wildcards, hostnames and pipes are not.</summary>
    public static bool IsLoopbackUrl(string url)
    {
        try
        {
            var address = BindingAddress.Parse(url);
            return !address.IsUnixPipe && !address.IsNamedPipe && IsLoopbackHost(address.Host);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True for <c>localhost</c> and loopback IP literals (IPv6 with or without brackets).</summary>
    public static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IsLoopback(ip));
    }

    /// <summary>True for an absolute origin whose host is loopback. <c>null</c> and anything unparsable are not.</summary>
    public static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);

    public static bool IsLoopback(IPAddress? address) =>
        address != null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
