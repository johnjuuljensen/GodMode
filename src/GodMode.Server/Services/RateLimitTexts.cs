using System.Globalization;

namespace GodMode.Server.Services;

/// <summary>What the server says of an account's rate limit (issue #596): to the user, once per account, and to a session it resumes.</summary>
public static class RateLimitTexts
{
    /// <summary>What a session the limit stopped is sent at the reset, when nothing of the user's was held for it.</summary>
    public const string Continuation =
        $"{ProjectManager.NoticePrefix} The account's rate limit stopped you; it has reset. Carry on from where you were.";

    /// <summary>"Kappe hit its five-hour limit; resumes at 15:50".</summary>
    public static string Hit(string? account, string? window, DateTime until, DateTime now) =>
        $"{account ?? "The account"} hit its {WindowName(window)}; resumes {When(until, now)}.";

    /// <summary>"Kappe's five-hour limit has reset; its sessions carry on".</summary>
    public static string Reset(string? account, string? window) =>
        $"{(account is { } named ? named + "'s" : "The account's")} {WindowName(window)} has reset; its sessions carry on.";

    /// <summary>Why something sent to a session under a limited account waits.</summary>
    public static string Held(DateTime until, DateTime now) =>
        $"its account is rate-limited, and it gets it when the limit resets, {When(until, now)}";

    /// <summary>The limit as claude's <c>rateLimitType</c> names it, in words.</summary>
    public static string WindowName(string? window) => window switch
    {
        "five_hour" => "five-hour limit",
        "seven_day" => "weekly limit",
        "seven_day_opus" => "weekly Opus limit",
        "seven_day_sonnet" => "weekly Sonnet limit",
        { Length: > 0 } other => $"{other.Replace('_', ' ')} limit",
        _ => "usage limit",
    };

    /// <summary>The reset in the server's local time: "at 15:50", or "on Monday at 15:50" when it is not within the day.</summary>
    public static string When(DateTime until, DateTime now)
    {
        var local = until.ToLocalTime();
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return until - now < TimeSpan.FromHours(20) ? $"at {time}" : $"on {local.ToString("dddd", CultureInfo.InvariantCulture)} at {time}";
    }
}
