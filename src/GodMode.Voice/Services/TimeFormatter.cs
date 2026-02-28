using System.Text.RegularExpressions;

namespace GodMode.Voice.Services;

public static class TimeFormatter
{
    /// <summary>
    /// Converts ISO 8601 timestamps in text to relative human-readable form.
    /// </summary>
    public static string HumanizeTimestamps(string text)
    {
        return Regex.Replace(text,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?",
            match =>
            {
                if (DateTimeOffset.TryParse(match.Value, out var dt))
                    return Relative(dt);
                return match.Value;
            });
    }

    /// <summary>
    /// Converts hh:mm:ss duration strings in text to natural English.
    /// </summary>
    public static string HumanizeUptimeStrings(string text)
    {
        return Regex.Replace(text, @"(\d{2}):(\d{2}):(\d{2})", match =>
        {
            if (int.TryParse(match.Groups[1].Value, out var h) &&
                int.TryParse(match.Groups[2].Value, out var m) &&
                int.TryParse(match.Groups[3].Value, out var s))
            {
                return HumanizeDuration(new TimeSpan(h, m, s));
            }
            return match.Value;
        });
    }

    public static string HumanizeDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 5) return "just started";
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds} seconds";
        if (ts.TotalMinutes < 2) return "about a minute";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes} minutes";
        if (ts.TotalHours < 2)
        {
            var mins = ts.Minutes;
            return mins > 0 ? $"about an hour and {mins} minutes" : "about an hour";
        }
        if (ts.TotalHours < 24) return $"{(int)ts.TotalHours} hours and {ts.Minutes} minutes";
        return $"{(int)ts.TotalDays} days and {ts.Hours} hours";
    }

    private static string Relative(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.UtcNow - dt;

        if (diff.TotalSeconds < 0) return "just now";
        if (diff.TotalSeconds < 10) return "just now";
        if (diff.TotalSeconds < 60) return $"{(int)diff.TotalSeconds} seconds ago";
        if (diff.TotalMinutes < 2) return "about a minute ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours < 2) return "about an hour ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hours ago";
        if (diff.TotalDays < 2) return "yesterday";
        return $"{(int)diff.TotalDays} days ago";
    }
}
