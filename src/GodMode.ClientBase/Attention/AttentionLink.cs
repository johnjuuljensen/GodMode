namespace GodMode.ClientBase.Attention;

/// <summary>
/// Names one attention item across servers: a project on a server. Both IDs are opaque (a project ID
/// contains '/', a codespace name is not a GUID), so the text form escapes each before joining them
/// with ':'. It is the key of the item's notification and, as the one path segment of
/// <c>godmode://attention/{key}</c>, the deep link a tap on it opens.
/// </summary>
public sealed record AttentionLink(string ServerId, string ProjectId)
{
    public const string Scheme = "godmode";
    public const string Host = "attention";

    /// <summary><c>{escaped serverId}:{escaped projectId}</c>; one URI path segment.</summary>
    public string Key => $"{Uri.EscapeDataString(ServerId)}:{Uri.EscapeDataString(ProjectId)}";

    public Uri ToUri() => new($"{Scheme}://{Host}/{Key}");

    /// <summary>The link a key names, or null when it is not one.</summary>
    public static AttentionLink? FromKey(string? key) =>
        key?.Split(':') is [var server, var project] && Unescape(server) is { } serverId && Unescape(project) is { } projectId
            ? new AttentionLink(serverId, projectId)
            : null;

    /// <summary>The link a <c>godmode://attention/{key}</c> URI names, or null when it names none.</summary>
    public static AttentionLink? FromUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Scheme && parsed.Host == Host
        && parsed.AbsolutePath.TrimStart('/').Split('/') is [var key]
            ? FromKey(key)
            : null;

    private static string? Unescape(string part) =>
        part.Length > 0 ? Uri.UnescapeDataString(part) : null;
}
