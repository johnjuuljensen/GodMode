using System.Text.Json;
using GodMode.ProjectFiles;
using GodMode.Shared;

namespace GodMode.Server.Services;

/// <summary>
/// The server's record of what a session was started as, for its fleet tools: <c>{root}/logs/{id}.fleet</c>,
/// beside its create log, out of its working folder. Its action, and whether the session that started it
/// granted it the fleet's tools. Written once, by its create or adopt, and never from anything the session
/// keeps: its <c>settings.json</c> is in its working folder, which it can write, and names its action too, so
/// a session that rewrote it would otherwise be another action's after a restart. A session without the record
/// (one made before it was kept) has no grant. The grant itself is the root's config's, read on every call
/// (<see cref="ProjectManager.HasFleetTools"/>): the record only says what to read it for.
/// </summary>
public static class FleetGrantFile
{
    public const string Extension = ".fleet";

    /// <param name="Action">The action the session was created or adopted with.</param>
    /// <param name="Granted">Whether the session that started it granted it the fleet's tools.</param>
    public sealed record Grant(string Action, bool Granted);

    public static string PathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, ProjectFolder.ScriptLogsFolderName, sessionId + Extension);

    /// <summary>Atomic, and replacing any record a session with the same id had before.</summary>
    public static void Write(string rootPath, string sessionId, Grant grant)
    {
        var path = PathFor(rootPath, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(grant, JsonDefaults.Compact));
    }

    /// <summary>The session's record; null when it has none, or one that cannot be read, which grants nothing.</summary>
    public static Grant? Read(string rootPath, string sessionId)
    {
        try
        {
            return JsonSerializer.Deserialize<Grant>(File.ReadAllText(PathFor(rootPath, sessionId)), JsonDefaults.Compact) is { Action.Length: > 0 } grant
                ? grant
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
