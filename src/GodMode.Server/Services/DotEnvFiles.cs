namespace GodMode.Server.Services;

/// <summary>
/// The <c>.env</c> files above a root (issue #614): one file per folder tree names the account
/// (<c>CLAUDE_CONFIG_DIR</c>) and anything else every tool that reads the convention shares. Every
/// <c>.env</c> from the root's own folder up to the drive's root is read, and a key a nearer file sets
/// replaces the same key from a farther one. Its layer lies over the profile's environment and under
/// the root's <c>environment</c> (<see cref="RootConfigReader"/> lays it there), read fresh on each
/// read of the root's config.
/// </summary>
public static class DotEnvFiles
{
    public const string FileName = ".env";

    /// <summary>
    /// The variables of the <c>.env</c> files from <paramref name="rootPath"/> upward, nearer winning; null when none sets one.
    /// A line that does not parse is left out and logged, as is a key naming the server's own secrets
    /// (<see cref="EnvironmentExpander.IsServerSecret"/>), and a file that cannot be read.
    /// </summary>
    public static Dictionary<string, string>? Read(string rootPath, ILogger logger)
    {
        Dictionary<string, string>? env = null;
        // Farthest first, so a nearer file's key replaces it
        foreach (var file in FilesAbove(rootPath).Reverse())
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{File} cannot be read, and gives no environment: {Reason}", file, ex.Message);
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                switch (ParseLine(lines[i]))
                {
                    case null:
                        continue;
                    case { Key: null }:
                        logger.LogWarning("{File}:{Line} is not KEY=VALUE, and is ignored", file, i + 1);
                        break;
                    case { Key: { } key } when EnvironmentExpander.IsServerSecret(key):
                        logger.LogWarning("{File}:{Line} sets {Key}, one of the server's own secrets, which no session or script gets: it is ignored",
                            file, i + 1, key);
                        break;
                    case { Key: { } key, Value: var value }:
                        (env ??= [])[key] = value;
                        break;
                }
            }
        }
        return env;
    }

    /// <summary>The key that names a root's profile (issue #616; WORKSTREAM, the user's name for a major line of work across their tools), under its <c>config.json</c>'s <c>profileName</c> and over its explicit entry's <c>Profile</c>.</summary>
    public const string ProfileKey = "WORKSTREAM";

    /// <summary>The profile <paramref name="dotEnv"/> names (<see cref="ProfileKey"/>), trimmed; null when it names none.</summary>
    public static string? ProfileOf(IReadOnlyDictionary<string, string>? dotEnv) =>
        dotEnv?.GetValueOrDefault(ProfileKey)?.Trim() is { Length: > 0 } profile ? profile : null;

    /// <summary>The <c>.env</c> files from <paramref name="rootPath"/> up to its drive's root, nearest first.</summary>
    internal static IEnumerable<string> FilesAbove(string rootPath)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(rootPath)); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, FileName);
            if (File.Exists(file)) yield return file;
        }
    }

    /// <summary>
    /// One dotenv line: null for a blank line or a comment, a null key for one that does not parse. <c>KEY=VALUE</c>,
    /// an optional <c>export </c> before the key, and a value optionally in single or double quotes (kept as written
    /// within them); an unquoted value ends at a <c> #</c> comment and is trimmed.
    /// </summary>
    internal static (string? Key, string Value)? ParseLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#') return null;
        if (text.StartsWith("export ", StringComparison.Ordinal)) text = text["export ".Length..].TrimStart();

        var equals = text.IndexOf('=');
        if (equals <= 0) return (null, "");
        var key = text[..equals].Trim();
        if (!IsKey(key)) return (null, "");

        var value = text[(equals + 1)..].Trim();
        if (value.Length > 0 && value[0] is '"' or '\'')
        {
            var close = value.IndexOf(value[0], 1);
            // An unclosed quote, or text after the closing one but a comment, does not parse
            if (close < 0 || value[(close + 1)..].Trim() is { Length: > 0 } rest && rest[0] != '#') return (null, "");
            return (key, value[1..close]);
        }
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (key, (comment >= 0 ? value[..comment] : value).TrimEnd());
    }

    private static bool IsKey(string key) =>
        (char.IsAsciiLetter(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
