using System.Collections;

namespace GodMode.Server.Services;

/// <summary>
/// Builds the environment a Claude process starts with. It does not inherit the server's
/// environment, which holds the server's own secrets: only the names on the allowlist pass
/// through, then the configured environment is laid on top (the profile's and root's
/// <c>environment</c>, already <c>${VAR}</c>-expanded, and the <c>GODMODE_*</c> variables the
/// server sets for this launch). Anything else a session needs must be named in config.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>Server variables a Claude process inherits. Matched case-insensitively.</summary>
    public static readonly IReadOnlySet<string> AllowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // OS essentials, all platforms
        "PATH", "HOME", "TEMP", "TMP", "TMPDIR", "LANG", "LANGUAGE", "TZ", "TERM",
        "USER", "LOGNAME", "SHELL", "HOSTNAME",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR",

        // OS essentials, Windows
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "OS",
        "USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "COMPUTERNAME", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "PSModulePath",

        // Network: proxies and trusted certificates
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR",

        // Tooling
        "DOTNET_ROOT",

        // Claude Code's own credentials and configuration: the process cannot run without them
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_GIT_BASH_PATH",
    };

    /// <summary>Prefixes of server variables a Claude process inherits (the locale categories).</summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes = ["LC_"];

    public static bool IsAllowed(string name) =>
        AllowedNames.Contains(name) || AllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The allowlisted part of <paramref name="serverEnvironment"/>, then every entry of
    /// <paramref name="configured"/>, which wins on a clash.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        IEnumerable<KeyValuePair<string, string?>> serverEnvironment,
        IReadOnlyDictionary<string, string>? configured)
    {
        // Windows variable names are case-insensitive; one entry per name there
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (name, value) in serverEnvironment)
            if (value != null && IsAllowed(name))
                env[name] = value;

        if (configured != null)
            foreach (var (name, value) in configured)
                env[name] = value;

        return env;
    }

    /// <summary>The server process's environment.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Current() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(e => new KeyValuePair<string, string?>((string)e.Key, (string?)e.Value));
}
