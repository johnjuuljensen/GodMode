namespace GodMode.Server.Services;

/// <summary>
/// The claude permission modes a root may pick with <c>permissionMode</c>, passed as
/// <c>--permission-mode</c>. <c>bypassPermissions</c> is not one of them: skipping permissions is
/// governed by <c>allowSkipPermissions</c> alone.
/// </summary>
public static class PermissionModes
{
    public const string BypassPermissions = "bypassPermissions";

    /// <summary>As claude spells them.</summary>
    public static readonly IReadOnlyList<string> Allowed = ["acceptEdits", "auto", "manual", "dontAsk", "plan"];

    /// <summary>The mode as claude spells it, whatever its case; null when it is not one a root may pick.</summary>
    public static string? Canonical(string mode) =>
        Allowed.FirstOrDefault(allowed => allowed.Equals(mode.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="mode"/> is not one a root may pick.</summary>
    public static string Refusal(string mode) =>
        mode.Trim().Equals(BypassPermissions, StringComparison.OrdinalIgnoreCase)
            ? $"permissionMode '{mode}' is refused: skipping permissions is allowSkipPermissions' to allow"
            : $"permissionMode '{mode}' is not one of {string.Join(", ", Allowed)}";
}
