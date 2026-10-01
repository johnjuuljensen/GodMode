using System.Text;

namespace GodMode.Server.Services;

/// <summary>
/// A session's address in Claude Code's own cross-session channel: the name GodMode gives its claude with <c>-n</c> on
/// every launch, resume included, so <c>ListAgents</c> lists it under it and <c>SendMessage</c> reaches it by it, from
/// another session in the same <c>CLAUDE_CONFIG_DIR</c>. It is <c>{root}-{id}</c>: the root's name, which is one root's
/// on a server, then the session's id, unique in its root; anything but a letter, a digit, <c>.</c>, <c>_</c> or
/// <c>-</c> in the root's name is a <c>-</c>. Stable for the session's life. A root's own <c>-n</c> or <c>--name</c> in its
/// <c>claudeArgs</c> is taken out (<see cref="WithoutName"/>): GodMode's name wins, so the address is the one it reports.
/// </summary>
public static class SessionAddress
{
    /// <summary>The variable a session's claude and scripts find its own address in.</summary>
    public const string Variable = "GODMODE_SESSION_ADDRESS";

    /// <summary>The variable a child's claude and create scripts find its parent's address in, beside <see cref="ProjectManager.ParentIdVariable"/>.</summary>
    public const string ParentVariable = "GODMODE_PARENT_ADDRESS";

    public static string Of(string? rootName, string sessionId)
    {
        if (string.IsNullOrEmpty(rootName)) return sessionId;
        var root = new StringBuilder(rootName.Length);
        foreach (var c in rootName) root.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        return $"{root}-{sessionId}";
    }

    /// <summary>The session's address, from its opaque ID (<c>{profile}/{root}/{id}</c>); null for an ID of another shape.</summary>
    public static string? OfId(string projectId) =>
        projectId.Split('/') is [_, var root, var id] ? Of(root, id) : null;

    /// <summary>
    /// <paramref name="args"/> without a name (<c>-n x</c>, <c>--name x</c>, <c>--name=x</c>); <paramref name="removed"/>
    /// says whether it had one.
    /// </summary>
    public static string[] WithoutName(IReadOnlyList<string> args, out bool removed)
    {
        var kept = new List<string>(args.Count);
        removed = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-n" or "--name")
            {
                removed = true;
                i++;
                continue;
            }
            if (args[i].StartsWith("--name=", StringComparison.Ordinal))
            {
                removed = true;
                continue;
            }
            kept.Add(args[i]);
        }
        return kept.ToArray();
    }
}
