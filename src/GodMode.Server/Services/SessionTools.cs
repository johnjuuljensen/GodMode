namespace GodMode.Server.Services;

/// <summary>
/// GodMode's own session tools on <c>/mcp</c> that every launch allows, in every root and permission mode, so no session
/// asks the user for them (the user's decision on issue #384): <c>message_parent</c> and <c>speak</c>, which every session
/// is asked to call on every turn. They reach nothing but the server: a message to the session's parent, and a spoken
/// text it checks. Nothing else is pre-approved: the permission prompt is not (claude calls it itself), the fleet's tools
/// are a root's to allow, and every other tool call reaches the permission prompt as before.
/// </summary>
public static class SessionTools
{
    /// <summary>How claude names <c>message_parent</c>: <c>mcp__godmode__message_parent</c>.</summary>
    public const string MessageParent = $"mcp__{ProjectManager.McpServerName}__{MessageParentTool.Name}";

    /// <summary>The tools every launch allows, as claude names them.</summary>
    public static readonly IReadOnlyList<string> Allowed = [MessageParent, SpokenReply.ToolName];

    private const string Flag = "--allowedTools";
    private const string KebabFlag = "--allowed-tools";

    /// <summary>
    /// A root's <paramref name="claudeArgs"/> with <see cref="Allowed"/> allowed too, in one <c>--allowedTools</c> list:
    /// added to the root's own, the first it has (<c>--allowedTools a b</c>, <c>--allowed-tools</c>, or
    /// <c>--allowedTools=a</c>), so no claude has to combine two of them; else a flag of its own after the root's args,
    /// which the server's own flags follow. A tool the root lists already is not listed again.
    /// </summary>
    public static List<string> Allow(IEnumerable<string>? claudeArgs)
    {
        var args = claudeArgs?.ToList() ?? [];
        string[] missing = [.. Allowed.Where(tool => !args.Contains(tool, StringComparer.Ordinal))];
        if (missing.Length == 0)
            return args;

        if (args.FindIndex(arg => arg is Flag or KebabFlag) is var at and >= 0)
            args.InsertRange(at + 1, missing);
        else if (args.FindIndex(arg => arg.StartsWith(Flag + "=", StringComparison.Ordinal) || arg.StartsWith(KebabFlag + "=", StringComparison.Ordinal)) is var joined and >= 0)
        {
            var value = args[joined][(args[joined].IndexOf('=') + 1)..];
            args[joined] = Flag;
            args.InsertRange(joined + 1, [.. missing, value]);
        }
        else
            args.AddRange([Flag, .. missing]);
        return args;
    }
}
