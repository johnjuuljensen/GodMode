namespace GodMode.Server.Services;

/// <summary>
/// The claude effort levels a root, an action or a create may pick with <c>effort</c>, passed as
/// <c>--effort</c>. Claude warns about any other value and runs at its default, so GodMode refuses it.
/// </summary>
public static class Efforts
{
    /// <summary>As claude spells them.</summary>
    public static readonly IReadOnlyList<string> Allowed = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>The level as claude spells it, whatever its case; null when it is not one.</summary>
    public static string? Canonical(string effort) =>
        Allowed.FirstOrDefault(allowed => allowed.Equals(effort.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="effort"/> is not a level.</summary>
    public static string Refusal(string effort) => $"effort '{effort}' is not one of {string.Join(", ", Allowed)}";
}
