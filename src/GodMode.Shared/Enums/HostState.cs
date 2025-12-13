namespace GodMode.Shared.Enums;

/// <summary>
/// Represents the current state of a host provider.
/// </summary>
public enum HostState
{
    /// <summary>
    /// Host is running and active.
    /// </summary>
    Running,

    /// <summary>
    /// Host is stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Host is starting up.
    /// </summary>
    Starting,

    /// <summary>
    /// Host is shutting down.
    /// </summary>
    Stopping,

    /// <summary>
    /// Host state is unknown.
    /// </summary>
    Unknown
}
