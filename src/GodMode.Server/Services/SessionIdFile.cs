using GodMode.ProjectFiles;

namespace GodMode.Server.Services;

/// <summary>
/// <c>.godmode/session-id</c>: the claude session a resume names with <c>--resume</c>. Written with
/// the ID a launch asks for, then with the one claude reports in its <c>system/init</c>, and read on
/// recovery, so a restarted server resumes the session claude actually keeps.
/// </summary>
public static class SessionIdFile
{
    public const string FileName = "session-id";

    public static string PathFor(string projectPath) => Path.Combine(projectPath, ".godmode", FileName);

    /// <summary>The saved session ID, or null when there is none.</summary>
    public static async Task<string?> ReadAsync(string projectPath, CancellationToken cancellationToken = default) =>
        File.Exists(PathFor(projectPath)) && (await File.ReadAllTextAsync(PathFor(projectPath), cancellationToken)).Trim() is { Length: > 0 } id
            ? id
            : null;

    /// <summary>Atomic, so recovery never reads half an ID.</summary>
    public static Task WriteAsync(string projectPath, string sessionId, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(PathFor(projectPath), sessionId, cancellationToken: cancellationToken);
}
