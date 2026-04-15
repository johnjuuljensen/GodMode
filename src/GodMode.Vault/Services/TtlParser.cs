using System.Text.RegularExpressions;

namespace GodMode.Vault.Services;

public static partial class TtlParser
{
    public static TimeSpan? Parse(string? ttl)
    {
        if (string.IsNullOrWhiteSpace(ttl)) return null;

        var match = TtlRegex().Match(ttl.Trim());
        if (!match.Success) throw new FormatException($"Invalid TTL format: '{ttl}'. Expected e.g. '90d', '24h', '30m'.");

        var value = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "d" => TimeSpan.FromDays(value),
            "h" => TimeSpan.FromHours(value),
            "m" => TimeSpan.FromMinutes(value),
            _ => throw new FormatException($"Unknown TTL unit: '{match.Groups[2].Value}'")
        };
    }

    public static string? Format(TimeSpan? ttl) => ttl switch
    {
        null => null,
        { TotalDays: >= 1 } t when t.TotalDays % 1 == 0 => $"{(int)t.TotalDays}d",
        { TotalHours: >= 1 } t when t.TotalHours % 1 == 0 => $"{(int)t.TotalHours}h",
        { } t => $"{(int)t.TotalMinutes}m"
    };

    [GeneratedRegex(@"^(\d+)(d|h|m)$")]
    private static partial Regex TtlRegex();
}
