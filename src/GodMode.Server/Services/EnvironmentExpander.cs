using System.Text.RegularExpressions;

namespace GodMode.Server.Services;

/// <summary>
/// Expands environment variable references in config-driven env dictionaries.
/// Supports two modes:
/// 1. Explicit ${VAR} expansion — resolves references from process env, skips entries with missing vars.
/// 2. Profile prefix stripping — scans process env for PROFILE_PREFIX_* vars and strips the prefix.
/// </summary>
public static partial class EnvironmentExpander
{
    /// <summary>
    /// Expands ${VAR} references in environment dictionary values.
    /// If a referenced variable does not exist in the process environment, the entire entry is removed
    /// (letting the ambient process env flow through naturally).
    /// </summary>
    public static Dictionary<string, string>? ExpandVariables(Dictionary<string, string>? env)
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

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex VarRefPattern();
}
