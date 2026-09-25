namespace GodMode.Server.Models;

/// <summary>
/// Everything one claude launch is given beyond the process manager's fixed flags: the configured
/// environment (profile, action) and the arguments (the action's, permissions, model, the MCP
/// config with GodMode's MCP endpoint and the launch's project token in it). Create and resume
/// launch from the same spec; they differ only in <c>--session-id</c> vs <c>--resume</c>, which
/// the process manager adds.
/// </summary>
public sealed record ClaudeLaunchSpec(Dictionary<string, string> Environment, string[] Args);

/// <summary>
/// A project's launch configuration cannot be built as it was created: its root config cannot be
/// read, or no longer has its action. Launching with the default action instead would drop the
/// action's environment and arguments (its account's <c>CLAUDE_CONFIG_DIR</c>, say).
/// </summary>
public sealed class LaunchConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server is stopping, and launches nothing more: a process started now would outlive it.</summary>
public sealed class ServerStoppingException() : InvalidOperationException("The server is stopping");
