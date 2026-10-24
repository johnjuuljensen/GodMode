namespace GodMode.Shared.Models;

/// <summary>
/// One usage window of a Claude account (issue #600), as claude last reported it: how much of it is used, 0 to 1, and when
/// it resets. A window whose <paramref name="ResetsAt"/> has passed has reset: it is 0 until the account's next turn says more.
/// </summary>
public record UsageWindow(double Utilization, DateTime? ResetsAt);

/// <summary>
/// What is known of one Claude account's usage (issue #600): its session and weekly windows (claude's <c>five_hour</c> and <c>seven_day</c>), from the
/// newest <c>rate_limit_event</c> any of the server's sessions on it wrote. Only as fresh as the account's last turn
/// (<paramref name="ObservedAt"/>): an idle account keeps its last reading, an upper bound. A live reading, not kept over a
/// restart of the server.
/// </summary>
/// <param name="Account">
/// The account's key, one per Claude config dir (<c>CLAUDE_CONFIG_DIR</c>), which <see cref="ProjectRootInfo.Account"/> and
/// <see cref="ProfileInfo.Account"/> name. Opaque, and per server: two servers' keys are not comparable.
/// </param>
/// <param name="Name">What the account is shown as: its config dir's folder name (<c>.claude-private</c>).</param>
/// <param name="Session">The current session's window (claude's <c>five_hour</c>); null when claude has not reported one.</param>
/// <param name="Weekly">The current week's window (claude's <c>seven_day</c>); null when claude has not reported one.</param>
public record AccountUsage(string Account, string Name, UsageWindow? Session, UsageWindow? Weekly, DateTime ObservedAt);

/// <summary>
/// A usage ceiling (issue #600), per window, each 0 to 1: at or above it, no session starts or resumes in the root, and the
/// fleet starts none. Null for a window sets none. A root's (its <c>config.json</c>'s <c>usageCeiling</c>) wins over its
/// profile's (<c>Profiles:&lt;name&gt;:UsageCeiling</c>), window by window.
/// </summary>
public record UsageCeiling(double? Session = null, double? Weekly = null)
{
    /// <summary>This ceiling's windows, each where it sets one, else <paramref name="under"/>'s.</summary>
    public UsageCeiling Over(UsageCeiling? under) => new(Session ?? under?.Session, Weekly ?? under?.Weekly);

    /// <summary>
    /// The ceiling as config gives it, each window a fraction: a value over 1 is a percentage (<c>70</c> is <c>0.7</c>), and
    /// one that is not above 0 sets none. Null when it sets none.
    /// </summary>
    public static UsageCeiling? Of(double? session, double? weekly) =>
        (Fraction(session), Fraction(weekly)) is (null, null) ? null : new UsageCeiling(Fraction(session), Fraction(weekly));

    private static double? Fraction(double? value) => value switch
    {
        null or <= 0 => null,
        > 1 => value / 100,
        _ => value,
    };
}
