namespace GodMode.Server.Models;

/// <summary>
/// Everything one claude launch is given beyond the process manager's fixed flags: the configured
/// environment (profile, action, the MCP bridge's <c>GODMODE_*</c> variables) and the arguments
/// (the action's, permissions, model, the MCP config with the bridge in it). Create and resume
/// launch from the same spec; they differ only in <c>--session-id</c> vs <c>--resume</c>, which
/// the process manager adds.
/// </summary>
public sealed record ClaudeLaunchSpec(Dictionary<string, string> Environment, string[] Args);
