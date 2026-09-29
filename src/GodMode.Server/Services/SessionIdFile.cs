using GodMode.ProjectFiles;

namespace GodMode.Server.Services;

/// <summary>
/// <c>session-id</c> in a session's state folder (<c>.godmode/sessions/{id}/</c>): the claude session a resume names with <c>--resume</c>. Written with
/// the ID a launch asks for, then with the one claude reports in its <c>system/init</c>, and read on
/// recovery, so a restarted server resumes the session claude actually keeps.
/// </summary>
public static class SessionIdFile
{
    public const string FileName = "session-id";

    public static string PathFor(string statePath) => Path.Combine(statePath, FileName);

    /// <summary>
    /// Whether <paramref name="sessionId"/> is a session ID: a GUID, which is what the server asks
    /// for and what claude reports. Anything else never reaches the command line: the session can
    /// write the file itself, and a value such as <c>--settings=x</c> would be read as a flag.
    /// </summary>
    public static bool IsValid(string? sessionId) => Guid.TryParseExact(sessionId, "D", out _);

    /// <summary>
    /// The saved session ID, or null when there is none. One that is not <see cref="IsValid"/> is
    /// logged and is none, so the project starts a fresh session.
    /// </summary>
    public static async Task<string?> ReadAsync(string statePath, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PathFor(statePath))) return null;
        var id = (await File.ReadAllTextAsync(PathFor(statePath), cancellationToken)).Trim();
        if (id.Length == 0) return null;
        if (IsValid(id)) return id;
        logger.LogWarning("The session id saved for {StatePath} is not a GUID; the project starts a fresh session", statePath);
        return null;
    }

    /// <summary>Atomic, so recovery never reads half an ID.</summary>
    public static Task WriteAsync(string statePath, string sessionId, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(PathFor(statePath), sessionId, cancellationToken: cancellationToken);
}
