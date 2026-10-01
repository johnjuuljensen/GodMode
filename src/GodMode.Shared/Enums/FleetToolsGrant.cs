namespace GodMode.Shared.Enums;

/// <summary>
/// Whether an action's sessions get the fleet's tools, its config's <c>fleetTools</c>: <c>true</c>,
/// <c>"grantable"</c>, or <c>false</c> (the default). The root's config is read for it on every call of
/// a fleet tool: a session's own files never grant it.
/// </summary>
public enum FleetToolsGrant
{
    /// <summary>No session of the action gets them (<c>false</c>, or no <c>fleetTools</c>).</summary>
    None,

    /// <summary>A session of the action gets them when the session that started it granted them, itself granted (<c>"grantable"</c>).</summary>
    Grantable,

    /// <summary>Every session of the action gets them (<c>true</c>); a grant at its start changes nothing.</summary>
    Granted,
}
