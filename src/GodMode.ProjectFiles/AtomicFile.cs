using System.Text;

namespace GodMode.ProjectFiles;

/// <summary>
/// Replaces a file's contents in one step: the text goes to a temp file beside it, which is then
/// moved over the target. A reader sees the old file or the new one, never a half-written one.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// On Windows the move fails while another process has the target open without
    /// <see cref="FileShare.Delete"/> (a plain <c>File.ReadAllText</c>, an editor, a virus scanner).
    /// Such readers hold it for milliseconds, so the move is retried for about half a second.
    /// </summary>
    private const int MoveAttempts = 50;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(10);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static async Task WriteAllTextAsync(string path, string contents, Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var temp = TempPathFor(path);
        try
        {
            await File.WriteAllTextAsync(temp, contents, encoding ?? Utf8NoBom, cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (Exception ex) when (IsHeldOpen(ex) && attempt < MoveAttempts)
                {
                    await Task.Delay(MoveRetryDelay, cancellationToken);
                }
            }
        }
        finally { DeleteQuietly(temp); }
    }

    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        var temp = TempPathFor(path);
        try
        {
            File.WriteAllText(temp, contents, encoding ?? Utf8NoBom);
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (Exception ex) when (IsHeldOpen(ex) && attempt < MoveAttempts) { Thread.Sleep(MoveRetryDelay); }
            }
        }
        finally { DeleteQuietly(temp); }
    }

    /// <summary>Same directory, so the move is a rename on one volume.</summary>
    private static string TempPathFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static bool IsHeldOpen(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>Only left behind when the write or the move failed.</summary>
    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* best effort: a stray temp file is harmless */ }
    }
}
