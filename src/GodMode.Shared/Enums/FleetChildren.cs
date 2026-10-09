namespace GodMode.Shared.Enums;

/// <summary>
/// Who is the parent of a session that an action's session starts with the fleet's <c>start_session</c>, its config's
/// <c>fleetChildren</c> (issue #431): <c>"own"</c> (the default) or <c>"topLevel"</c>. The root's config is read for it
/// on every call of <c>start_session</c>, as it is for <see cref="FleetToolsGrant"/>.
/// </summary>
public enum FleetChildren
{
    /// <summary>The caller is the new session's parent, unless it passes <c>top_level</c> or names another <c>parent</c> (<c>"own"</c>, or no <c>fleetChildren</c>).</summary>
    Own,

    /// <summary>
    /// Every session it starts is top level, whatever <c>top_level</c> says, and naming a <c>parent</c> is refused
    /// (<c>"topLevel"</c>): a short-lived chat that starts overseers is no parent to them.
    /// </summary>
    TopLevel,
}
