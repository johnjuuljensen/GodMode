using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Services;

/// <summary>
/// Expands environment variable references in config-driven env dictionaries.
/// Supports two modes:
/// 1. Explicit ${VAR} expansion — resolves references from process env, skips entries with missing vars.
/// 2. Profile prefix stripping — scans process env for PROFILE_PREFIX_* vars and strips the prefix.
/// Neither reads the server's own secrets (<see cref="IsServerSecret"/>): the child environment is an
/// allowlist (<see cref="ChildEnvironment"/>), and config must not be a way around it.
/// </summary>
public static partial class EnvironmentExpander
{
    /// <summary>
    /// The configuration section of the server's own secrets: its API key (<c>Authentication:ApiKey</c>)
    /// and where it keeps it, in the forms an environment variable names them (<c>Authentication__ApiKey</c>,
    /// <c>Authentication:ApiKey</c>), matched case-insensitively.
    /// </summary>
    private static readonly string[] ServerSecretPrefixes = ["Authentication__", "Authentication:"];

    /// <summary>
    /// The host's environment prefixes: <c>WebApplication.CreateBuilder</c> reads <c>ASPNETCORE_Authentication__ApiKey</c>
    /// and <c>DOTNET_Authentication__ApiKey</c> into <c>Authentication:ApiKey</c> too, so under either the key is still the key.
    /// </summary>
    private static readonly string[] HostPrefixes = ["ASPNETCORE_", "DOTNET_"];

    /// <summary>The names already logged as refused: each is logged once per server process.</summary>
    private static readonly ConcurrentDictionary<string, byte> LoggedRefusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a refusal is logged. The server sets it at its start.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Whether <paramref name="name"/> is one of the server's own secrets, which no config may pass to a child.</summary>
    public static bool IsServerSecret(string name)
    {
        var unprefixed = HostPrefixes.FirstOrDefault(host => name.StartsWith(host, StringComparison.OrdinalIgnoreCase)) is { } hostPrefix
            ? name[hostPrefix.Length..]
            : name;
        return ServerSecretPrefixes.Any(prefix => unprefixed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Expands ${VAR} references in environment dictionary values.
    /// If a referenced variable does not exist in the process environment, the entire entry is removed
    /// (letting the ambient process env flow through naturally).
    /// A reference to one of the server's own secrets (<see cref="IsServerSecret"/>) expands to an empty
    /// string, whether it is set or not, and is logged once (<paramref name="logger"/>, else <see cref="Logger"/>).
    /// </summary>
    public static Dictionary<string, string>? ExpandVariables(Dictionary<string, string>? env, ILogger? logger = null)
    {
        if (env is not { Count: > 0 }) return env;

        Dictionary<string, string>? result = null;

        foreach (var (key, value) in env)
        {
            if (!VarRefPattern().IsMatch(value))
            {
                // No ${VAR} references — keep as-is
                result ??= new Dictionary<string, string>(env.Count);
                result[key] = value;
                continue;
            }

            // Expand all ${VAR} references in the value
            var allResolved = true;
            var expanded = VarRefPattern().Replace(value, match =>
            {
                var varName = match.Groups[1].Value;
                if (IsServerSecret(varName))
                {
                    LogRefusalOnce(varName, key, logger);
                    return "";
                }

                var envValue = Environment.GetEnvironmentVariable(varName);
                if (envValue != null) return envValue;

                allResolved = false;
                return match.Value; // placeholder, won't be used
            });

            if (allResolved)
            {
                result ??= new Dictionary<string, string>(env.Count);
                result[key] = expanded;
            }
            // else: skip entry entirely — missing var means don't set the key
        }

        return result;
    }

    /// <summary>
    /// Checks whether prefix stripping is enabled for a profile, either via config flag
    /// or via a {PREFIX}STRIP_ENV_VAR_PROFILE env var (e.g. MEGA_STRIP_ENV_VAR_PROFILE=true).
    /// </summary>
    public static bool IsStripEnabled(string? profileName, bool configFlag)
    {
        if (configFlag) return true;
        if (string.IsNullOrWhiteSpace(profileName)) return false;

        var prefix = ProfileNameToPrefix(profileName);
        if (prefix.Length == 0) return false;

        var envValue = Environment.GetEnvironmentVariable($"{prefix}STRIP_ENV_VAR_PROFILE");
        return envValue is "true" or "True" or "1";
    }

    /// <summary>
    /// Scans process environment for variables with the given profile name as prefix,
    /// strips the prefix, and returns the stripped mappings.
    /// Profile name is converted to UPPER_SNAKE_CASE for the prefix (e.g. "My Profile" → "MY_PROFILE_").
    /// The control variable {PREFIX}STRIP_ENV_VAR_PROFILE is excluded from the results.
    /// </summary>
    public static Dictionary<string, string>? GetPrefixStrippedVars(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;

        var prefix = ProfileNameToPrefix(profileName);
        if (prefix.Length == 0) return null;

        Dictionary<string, string>? result = null;

        foreach (var entry in Environment.GetEnvironmentVariables())
        {
            if (entry is not System.Collections.DictionaryEntry { Key: string envKey, Value: string envValue })
                continue;

            if (!envKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var stripped = envKey[prefix.Length..];
            if (stripped.Length == 0) continue;

            // Don't inject the control variable itself
            if (stripped.Equals("STRIP_ENV_VAR_PROFILE", StringComparison.OrdinalIgnoreCase))
                continue;

            // Nor one of the server's own secrets, under a profile that happens to share its prefix
            if (IsServerSecret(envKey))
            {
                LogRefusalOnce(envKey, stripped, logger: null);
                continue;
            }

            result ??= new Dictionary<string, string>();
            result[stripped] = envValue;
        }

        return result;
    }

    /// <summary>
    /// Converts a profile name to an env var prefix: uppercase, spaces/hyphens → underscore, trailing underscore.
    /// "My Profile" → "MY_PROFILE_", "mega" → "MEGA_"
    /// </summary>
    internal static string ProfileNameToPrefix(string profileName)
    {
        var sb = new System.Text.StringBuilder(profileName.Length + 1);
        foreach (var c in profileName)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
            else if (c is ' ' or '-' or '.')
                sb.Append('_');
            // skip other characters
        }

        if (sb.Length == 0) return "";
        sb.Append('_');
        return sb.ToString();
    }

    private static void LogRefusalOnce(string secretName, string entry, ILogger? logger)
    {
        if (LoggedRefusals.TryAdd(secretName, 0))
            (logger ?? Logger).LogWarning(
                "Config environment entry {Entry} refers to the server's own secret {Secret}, which no session or script gets: it is left empty. Name the value in the root's or profile's environment itself",
                entry, secretName);
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex VarRefPattern();
}
