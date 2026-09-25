using System.Collections;

namespace GodMode.Server.Services;

/// <summary>
/// Builds the environment a process the server starts runs with: a Claude process, or a root script
/// (prepare, create, delete, status). Neither inherits the server's environment, which holds the
/// server's own secrets (an <c>Authentication__ApiKey</c>, a codespace's <c>GITHUB_TOKEN</c>): only
/// the names on its allowlist pass through, then the configured environment is laid on top (the
/// profile's and root's <c>environment</c>, already <c>${VAR}</c>-expanded, and the <c>GODMODE_*</c>
/// variables the server sets). Anything else a session or a script needs, a credential such as
/// <c>GH_TOKEN</c> or <c>SSH_AUTH_SOCK</c> included, must be named in config.
/// </summary>
public sealed class ChildEnvironment
{
    /// <summary>What any process needs to run: the OS essentials, proxies and certificates, and tooling.</summary>
    private static readonly string[] Essentials =
    [
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
    ];

    /// <summary>Claude Code's own credentials and configuration: a Claude process cannot run without them.</summary>
    private static readonly string[] ClaudeCode =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_GIT_BASH_PATH",
    ];

    /// <summary>A Claude process: the essentials and Claude Code's own credentials.</summary>
    public static readonly ChildEnvironment Claude = new([.. Essentials, .. ClaudeCode]);

    /// <summary>A root script: the essentials alone. It has no use for Claude Code's credentials.</summary>
    public static readonly ChildEnvironment Script = new(Essentials);

    /// <summary>Prefixes of server variables every child inherits (the locale categories).</summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes = ["LC_"];

    private ChildEnvironment(IEnumerable<string> allowedNames) =>
        AllowedNames = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>Server variables this child inherits. Matched case-insensitively.</summary>
    public IReadOnlySet<string> AllowedNames { get; }

    public bool IsAllowed(string name) =>
        AllowedNames.Contains(name) || AllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The allowlisted part of <paramref name="serverEnvironment"/>, then every entry of
    /// <paramref name="configured"/>, which wins on a clash.
    /// </summary>
    public IReadOnlyDictionary<string, string> Build(
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
