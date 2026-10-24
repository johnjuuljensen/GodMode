namespace GodMode.Shared.Models;

/// <summary>
/// Information about a server-defined profile.
/// </summary>
/// <param name="Name">The profile name.</param>
/// <param name="Description">Optional description of the profile.</param>
/// <param name="Account">
/// The Claude account its sessions run on (issue #600), by its environment's <c>CLAUDE_CONFIG_DIR</c>: the key of its
/// <see cref="AccountUsage"/>. A root of it whose own environment names another has its own <see cref="ProjectRootInfo.Account"/>.
/// </param>
/// <param name="UsageCeiling">Its usage ceiling (<c>Profiles:&lt;name&gt;:UsageCeiling</c>); null when it has none.</param>
public record ProfileInfo(string Name, string? Description = null, string? Account = null, UsageCeiling? UsageCeiling = null);
