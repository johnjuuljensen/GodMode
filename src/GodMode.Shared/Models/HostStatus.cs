using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents the detailed status of a host.
/// </summary>
/// <param name="Id">Unique identifier for the host.</param>
/// <param name="Name">Display name of the host.</param>
/// <param name="Type">Type of host (e.g., "github", "local").</param>
/// <param name="State">Current state of the host.</param>
/// <param name="Url">The URL to connect to the host, if available.</param>
/// <param name="ProjectCount">Number of projects on this host.</param>
/// <param name="LastSeen">Last time the host was seen online.</param>
public record HostStatus(
    string Id,
    string Name,
    string Type,
    HostState State,
    string? Url = null,
    int ProjectCount = 0,
    DateTime? LastSeen = null
);
