using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents information about a host provider (e.g., Docker container, Codespace).
/// </summary>
/// <param name="Id">Unique identifier for the host.</param>
/// <param name="Name">Display name of the host.</param>
/// <param name="Type">Type of host (e.g., "github", "local").</param>
/// <param name="State">Current state of the host.</param>
/// <param name="Url">Optional URL to connect to the host.</param>
public record HostInfo(
    string Id,
    string Name,
    string Type,
    HostState State,
    string? Url = null
);
