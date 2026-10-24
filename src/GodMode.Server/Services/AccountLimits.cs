using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// One of an account's usage windows, as a <c>rate_limit_event</c>'s <c>unifiedWindows</c> gives it: how much of it is used
/// (0 to 1), and when it resets.
/// </summary>
public sealed record RateLimitWindow(double? Utilization, DateTime? ResetsAt);

/// <summary>
/// What claude says of its account's rate limit, a <c>rate_limit_event</c>'s <c>rate_limit_info</c> (issue #596): its
/// <paramref name="Status"/> (<c>allowed</c>, <c>allowed_warning</c>, <c>rejected</c>), when the limit it names resets,
/// which one it is (<c>five_hour</c>, <c>seven_day</c>, …), and every window it reports.
/// </summary>
public sealed record RateLimitInfo(string Status, DateTime? ResetsAt, string? Window, IReadOnlyDictionary<string, RateLimitWindow> Windows)
{
    /// <summary>The <c>type</c> of the line claude writes it in.</summary>
    public const string EventType = "rate_limit_event";

    /// <summary>The status of a request the limit refused: claude's turn ends in error after it.</summary>
    public const string Rejected = "rejected";

    public bool IsRejected => Status == Rejected;

    /// <summary>The line's info when it is a <c>rate_limit_event</c> with a status; null for any other line.</summary>
    public static RateLimitInfo? Parse(string jsonLine)
    {
        if (!jsonLine.Contains(EventType, StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || !type.ValueEquals(EventType)
                || !root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object
                || StringOf(info, "status") is not { } status)
                return null;
            var windows = new Dictionary<string, RateLimitWindow>(StringComparer.Ordinal);
            if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
                foreach (var window in unified.EnumerateObject().Where(w => w.Value.ValueKind == JsonValueKind.Object))
                    windows[window.Name] = new RateLimitWindow(
                        window.Value.TryGetProperty("utilization", out var used) && used.TryGetDouble(out var u) ? u : null,
                        EpochOf(window.Value, "resetsAt"));
            return new RateLimitInfo(status, EpochOf(info, "resetsAt"), StringOf(info, "rateLimitType"), windows);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary><c>resetsAt</c> is epoch seconds.</summary>
    private static DateTime? EpochOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
}

/// <summary>
/// The rate limits of the accounts the server's sessions run under (issue #596), fed by every <c>rate_limit_event</c> any
/// of them writes. An account is a Claude config dir (the session's effective <c>CLAUDE_CONFIG_DIR</c>, full path, see
/// <see cref="Models.ProjectInfo.ConfigDir"/>): every session under it shares its limits. A rejection marks it limited
/// until the reset it names, and the first one of a limit names the session that tells the user of it. In memory: what a
/// restart must keep is in the sessions' status.json (<see cref="Shared.Models.ProjectStatus.RateLimited"/>), which seeds it
/// again (<see cref="Seed"/>).
/// </summary>
public sealed class AccountLimits
{
    /// <summary>An account's limit: until when, which window, and the session that tells of it.</summary>
    public sealed record Limit(DateTime Until, string? Window, string AnnouncerId);

    /// <summary>
    /// What is known of one account: the limit it is under, or was last, whether its reset has been told, and the last
    /// info any session reported.
    /// </summary>
    private sealed record Account(Limit? Limit, RateLimitInfo? Last, bool ResetTold = false, Usage? Usage = null);

    /// <summary>An account's usage windows as last reported (issue #600), each kept until a report names it again, and when.</summary>
    private sealed record Usage(UsageWindow? Session, UsageWindow? Weekly, DateTime ObservedAt);

    /// <summary>The <c>unifiedWindows</c> names of the windows the usage readings keep: the session's and the week's, as claude names them.</summary>
    public const string SessionWindow = "five_hour", WeeklyWindow = "seven_day";

    private readonly ConcurrentDictionary<string, Account> _accounts =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Takes in what a session under <paramref name="account"/> reported. A rejection with a reset marks the account limited
    /// until then, or later when a later one comes; returns the new limit when this rejection started it (the account was
    /// not limited at <paramref name="now"/>), so its session tells of it. Null otherwise.
    /// </summary>
    public Limit? Observe(string account, RateLimitInfo info, string projectId, DateTime now)
    {
        Limit? started = null;
        _accounts.AddOrUpdate(account,
            _ => new Account(Started(null), info, Usage: UsageOf(null, info, now)),
            (_, known) => Started(known.Limit) is var limit && limit == known.Limit
                ? known with { Last = info, Usage = UsageOf(known.Usage, info, now) }
                : new Account(limit, info, Usage: UsageOf(known.Usage, info, now)));
        return started;

        // Called once per attempt: an attempt another update overtook is tried again, and its start does not count
        Limit? Started(Limit? limit)
        {
            started = null;
            if (!info.IsRejected || info.ResetsAt is not { } until) return limit;
            if (limit != null && limit.Until > now)
                return until > limit.Until ? limit with { Until = until, Window = info.Window } : limit;
            return started = new Limit(until, info.Window, projectId);
        }
    }

    /// <summary>The limit <paramref name="account"/> is under at <paramref name="now"/>; null when none holds it.</summary>
    public Limit? LimitOf(string account, DateTime now) =>
        _accounts.TryGetValue(account, out var known) && known.Limit is { } limit && limit.Until > now ? limit : null;

    /// <summary>
    /// The limit <paramref name="account"/> was under, once at <paramref name="now"/> it has reset and only the first time
    /// this is asked of it: its reset is told once, by whichever of its sessions comes to it first. Null otherwise.
    /// </summary>
    public Limit? TakeReset(string account, DateTime now)
    {
        while (_accounts.TryGetValue(account, out var known))
        {
            if (known is not { Limit: { } limit, ResetTold: false } || limit.Until > now) return null;
            if (_accounts.TryUpdate(account, known with { ResetTold = true }, known)) return limit;
        }
        return null;
    }

    /// <summary>What a session under <paramref name="account"/> last reported of its limits.</summary>
    public RateLimitInfo? LastOf(string account) => _accounts.TryGetValue(account, out var known) ? known.Last : null;

    /// <summary>
    /// The usage of <paramref name="account"/> as last reported (issue #600), its windows as claude gave them: null when no
    /// session under it has reported a window. Whether a window has reset since is the reader's to say, at its own time.
    /// </summary>
    public AccountUsage? UsageOf(string account) =>
        _accounts.TryGetValue(account, out var known) && known.Usage is { } usage
            ? new AccountUsage(KeyOf(account), NameOf(account), usage.Session, usage.Weekly, usage.ObservedAt)
            : null;

    /// <summary>The usage of every account a session has reported a window of.</summary>
    public AccountUsage[] Usages() =>
        _accounts.Keys.Select(account => UsageOf(account)).OfType<AccountUsage>().OrderBy(usage => usage.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Whether <paramref name="info"/> changes the usage <paramref name="account"/> reported last: a window it names that
    /// differs from the one kept. The time of the report alone is no change.
    /// </summary>
    public bool ChangesUsage(string account, RateLimitInfo info)
    {
        var kept = _accounts.TryGetValue(account, out var known) ? known.Usage : null;
        return WindowOf(info, SessionWindow) is { } session && session != kept?.Session
            || WindowOf(info, WeeklyWindow) is { } weekly && weekly != kept?.Weekly;
    }

    /// <summary>
    /// The account's key the clients get (<see cref="AccountUsage.Account"/>): its config dir's path, hashed, so no path
    /// of the server's leaves it, and one account is one key across restarts.
    /// </summary>
    public static string KeyOf(string account) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? account.ToUpperInvariant() : account)))[..12];

    /// <summary>What the account is shown as: its config dir's folder name.</summary>
    public static string NameOf(string account) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(account)) is { Length: > 0 } name ? name : account;

    /// <summary>The kept usage with the windows <paramref name="info"/> reports over it; the kept usage when it reports none.</summary>
    private static Usage? UsageOf(Usage? kept, RateLimitInfo info, DateTime now) =>
        (WindowOf(info, SessionWindow), WindowOf(info, WeeklyWindow)) switch
        {
            (null, null) => kept,
            var (session, weekly) => new Usage(session ?? kept?.Session, weekly ?? kept?.Weekly, now),
        };

    private static UsageWindow? WindowOf(RateLimitInfo info, string name) =>
        info.Windows.TryGetValue(name, out var window) && window.Utilization is { } utilization
            ? new UsageWindow(utilization, window.ResetsAt)
            : null;

    /// <summary>
    /// Marks <paramref name="account"/> limited until <paramref name="until"/>, as a session's status.json says after a
    /// restart, unless it is under a later limit already.
    /// </summary>
    public void Seed(string account, DateTime until, string? window, string projectId) =>
        _accounts.AddOrUpdate(account,
            _ => new Account(new Limit(until, window, projectId), null),
            (_, known) => known.Limit is { } limit && limit.Until >= until ? known : known with { Limit = new Limit(until, window, projectId), ResetTold = false });
}
