using System.Text.Json;
using GodMode.ProjectFiles;
using GodMode.Shared;

namespace GodMode.Server.Services;

/// <summary>
/// The server's record of what a session was started as, for its fleet tools and its parent: <c>{root}/logs/{id}.fleet</c>,
/// beside its create log, out of its working folder. Its action, whether the session that started it granted
/// it the fleet's tools, and its working folder. Written once, by its create or adopt, and never from anything
/// the session keeps: its <c>settings.json</c> is in its working folder, which it can write, and names its action
/// too, so a session that rewrote it would otherwise be another action's after a restart: a launch takes its action
/// from here (issue #399), as its fleet tools do. The folder binds the
/// record to the session it was written for: the id is its state folder's name, which a session can make in its
/// own folder, so a state folder planted under the id elsewhere is not that session. The record leaves with the
/// session (a delete, a forget): set aside in the root's logs while its state is in the trash (<see cref="SetAside"/>),
/// deleted otherwise and by the trash's purge. A restore takes it back marked <see cref="Grant.Restored"/>, which grants
/// nothing. A session without the record (one made before it was kept) has no grant either. The grant itself is the root's
/// config's, read on every call (<see cref="ProjectManager.HasFleetTools"/>): the record only says what to read it for.
/// </summary>
public static class FleetGrantFile
{
    public const string Extension = ".fleet";

    /// <param name="Action">The action the session was created or adopted with.</param>
    /// <param name="Granted">Whether the session that started it granted it the fleet's tools.</param>
    /// <param name="Folder">Its working folder, relative to the root (<c>.</c> for the root itself), so the record survives a move of the root.</param>
    /// <param name="Parent">
    /// The ID of the session that started it, as its create named it (a re-key of the parent rewrites it); null at top
    /// level. The server's word for its parent, for <c>message_parent</c> and the notices: its <c>status.json</c>'s
    /// <c>ParentId</c>, which the session can write, only nests it in the app.
    /// </param>
    /// <param name="Restored">
    /// Brought back from the trash: it says the session's action, for its launch, and grants nothing, as a neighbour in
    /// a shared folder could have planted the trashed id in that very folder.
    /// </param>
    public sealed record Grant(string Action, bool Granted, string Folder, string? Parent = null, bool Restored = false);

    /// <summary>The extension of a record set aside while its session is in the trash (<see cref="SetAside"/>).</summary>
    public const string SetAsideExtension = ".fleet-trashed";

    public static string PathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, ProjectFolder.ScriptLogsFolderName, sessionId + Extension);

    public static string SetAsidePathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, ProjectFolder.ScriptLogsFolderName, sessionId + SetAsideExtension);

    /// <summary>Atomic, and replacing any record a session with the same id had before.</summary>
    public static void Write(string rootPath, string sessionId, Grant grant)
    {
        var path = PathFor(rootPath, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(grant, JsonDefaults.Compact));
    }

    /// <summary>The session's record; null when it has none, or one that cannot be read, which grants nothing.</summary>
    public static Grant? Read(string rootPath, string sessionId) => ReadAt(PathFor(rootPath, sessionId));

    private static Grant? ReadAt(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Grant>(File.ReadAllText(path), JsonDefaults.Compact) is { Action.Length: > 0, Folder.Length: > 0 } grant
                ? grant
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the session's record aside while its state is in the trash, in the root's logs beside it (never in the
    /// trash, which is in a folder sessions write), so a restore launches it as the action it was created with.
    /// </summary>
    public static void SetAside(string rootPath, string sessionId)
    {
        var path = PathFor(rootPath, sessionId);
        if (File.Exists(path)) File.Move(path, SetAsidePathFor(rootPath, sessionId), overwrite: true);
    }

    /// <summary>
    /// The record set aside for the session, restored: a record again, marked <see cref="Grant.Restored"/>, its
    /// starter's grant and its parent gone. Null when none was set aside, or it cannot be read.
    /// </summary>
    public static Grant? Restore(string rootPath, string sessionId)
    {
        var setAside = SetAsidePathFor(rootPath, sessionId);
        var grant = ReadAt(setAside) is { } kept ? kept with { Granted = false, Parent = null, Restored = true } : null;
        if (grant != null) Write(rootPath, sessionId, grant);
        File.Delete(setAside);
        return grant;
    }

    /// <summary>Deletes the session's record, and any set aside for it.</summary>
    public static void Delete(string rootPath, string sessionId)
    {
        File.Delete(PathFor(rootPath, sessionId));
        File.Delete(SetAsidePathFor(rootPath, sessionId));
    }
}
