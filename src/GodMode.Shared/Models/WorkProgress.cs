namespace GodMode.Shared.Models;

/// <summary>
/// How far a project's work has got, as its root's <c>status</c> script last reported it
/// (<see cref="ProjectStatus.Progress"/>, issue #545): an epic's sub-issues resolved, out of all of them. The script
/// decides what a unit is and when it counts as resolved; the server only holds <c>0 &lt;= Resolved &lt;= Total</c>.
/// </summary>
/// <param name="Resolved">How many are resolved.</param>
/// <param name="Total">How many there are.</param>
public record WorkProgress(int Resolved, int Total)
{
    /// <summary>Whether the numbers are ones the server keeps: whole, not negative, and no more resolved than there are.</summary>
    public bool IsValid => Resolved >= 0 && Total >= 0 && Resolved <= Total;
}
