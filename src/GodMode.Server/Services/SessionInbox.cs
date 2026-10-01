using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.ProjectFiles;
using GodMode.Shared;

namespace GodMode.Server.Services;

/// <summary>
/// The messages held for a session until it can take them: <c>{root}/logs/{id}.inbox.jsonl</c>, beside its create log
/// and its fleet record, out of every working folder, so no session (a neighbour in a shared folder, one whose folder
/// is its root) can write another's. One JSON line each, oldest first, as <see cref="HeldMessage"/>: who sent it and how,
/// never a label, which is made at delivery from the sender's ID. Kept on disk whatever the session is doing, so a
/// message is not lost to a stop or a restart; a delivery takes the ones it dealt with off the front. Deleted when the
/// session leaves GodMode, with its record. A line that cannot be read is skipped. The caller holds the session's
/// <see cref="Models.ProjectProcess.InboxLock"/>.
/// </summary>
public static class SessionInbox
{
    public const string Extension = ".inbox.jsonl";

    /// <summary>The most characters one message may have: <c>message_parent</c> and the fleet's <c>send</c> refuse a longer one.</summary>
    public const int MaxTextLength = 8000;

    /// <summary>The most messages held for one session at once: past it, a new one is refused.</summary>
    public const int MaxHeld = 50;

    /// <summary>The most characters of text held for one session at once: past it, a new message is refused.</summary>
    public const int MaxHeldChars = 64_000;

    /// <summary>How a held message was sent: with <c>message_parent</c>, by a child, or with the fleet's <c>send</c>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HeldKind>))]
    public enum HeldKind { Message, Send }

    /// <param name="At">When it was held.</param>
    /// <param name="From">The sending session's ID; null for a <c>send</c> with the server's credential (the user's own overseer).</param>
    /// <param name="Kind">How it was sent.</param>
    /// <param name="Text">The message, as its sender wrote it.</param>
    public sealed record HeldMessage(DateTime At, string? From, HeldKind Kind, string Text);

    public static string PathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, ProjectFolder.ScriptLogsFolderName, sessionId + Extension);

    public static bool Any(string rootPath, string sessionId) => new FileInfo(PathFor(rootPath, sessionId)) is { Exists: true, Length: > 0 };

    /// <summary>The held messages, oldest first; none without the file.</summary>
    public static IReadOnlyList<HeldMessage> Read(string rootPath, string sessionId)
    {
        var path = PathFor(rootPath, sessionId);
        if (!File.Exists(path)) return [];
        var messages = new List<HeldMessage>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HeldMessage>(line, JsonDefaults.Compact) is { Text: not null } message
                    && (message.From != null || message.Kind == HeldKind.Send))
                    messages.Add(message);
            }
            catch (JsonException) { }
        }
        return messages;
    }

    /// <summary>
    /// Appends <paramref name="message"/>, after what is held, unless that is full (<see cref="MaxHeld"/>,
    /// <see cref="MaxHeldChars"/>): then it throws <see cref="InvalidOperationException"/>, saying so. A last line left
    /// torn (a write cut short) is ended first, so the new one is a line of its own.
    /// </summary>
    public static void Append(string rootPath, string sessionId, HeldMessage message)
    {
        var held = Read(rootPath, sessionId);
        if (held.Count >= MaxHeld || held.Sum(m => m.Text.Length) + message.Text.Length > MaxHeldChars)
            throw new InvalidOperationException(
                $"The receiver already has {held.Count} message(s) held for it ({held.Sum(m => m.Text.Length)} characters; at most {MaxHeld} and {MaxHeldChars}): " +
                "nothing more is held until it takes them. Send this later, or put it on the issue or pull request.");

        var path = PathFor(rootPath, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var torn = new FileInfo(path) is { Exists: true, Length: > 0 } && !EndsWithNewline(path);
        File.AppendAllText(path, (torn ? "\n" : "") + JsonSerializer.Serialize(message, JsonDefaults.Compact) + "\n");
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    /// <summary>Takes the first <paramref name="count"/> messages off, as <see cref="Read"/> gave them; the file goes when none is left.</summary>
    public static void RemoveFirst(string rootPath, string sessionId, int count)
    {
        if (count <= 0) return;
        var rest = Read(rootPath, sessionId).Skip(count).ToList();
        var path = PathFor(rootPath, sessionId);
        if (rest.Count == 0)
        {
            File.Delete(path);
            return;
        }
        AtomicFile.WriteAllText(path, string.Concat(rest.Select(message => JsonSerializer.Serialize(message, JsonDefaults.Compact) + "\n")));
    }

    /// <summary>Deletes what is held for the session, if anything is.</summary>
    public static void Delete(string rootPath, string sessionId) => File.Delete(PathFor(rootPath, sessionId));

    /// <summary>
    /// The label a message from a session carries, so it cannot pass for the user's own words:
    /// <c>[Message from session {id} "{name}"]</c>, the name on one line; without the name when the sender is gone.
    /// </summary>
    public static string LabelOf(string sessionId, string? name) =>
        name == null ? $"[Message from session {sessionId}]" : $"[Message from session {sessionId} \"{OneLine(name, 100)}\"]";

    /// <summary>The label of a message the fleet's <c>send</c> held from the server's own credential: the user's own overseer.</summary>
    public const string OverseerLabel = "[Message from the overseer on the fleet's endpoint]";

    /// <summary>
    /// A held message, as the receiver reads it: its label, then its text, each line of which that could pass for a label
    /// (<c>[Message from …</c>, <c>[GodMode notice] …</c>) quoted with <c>&gt; </c>.
    /// </summary>
    public static string Render(string label, string text) =>
        $"{label}\n{string.Join("\n", text.Split('\n').Select(line => LooksLikeALabel(line) ? "> " + line : line))}";

    private static bool LooksLikeALabel(string line) =>
        line.TrimStart() is var start && (start.StartsWith("[Message from", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith(ProjectManager.NoticePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything held, as one input: the rendered messages, oldest first, then the notices on a line each. Null when nothing is.</summary>
    public static string? Compose(IEnumerable<string> messages, IEnumerable<string> notices)
    {
        var parts = messages.ToList();
        if (notices.ToList() is { Count: > 0 } lines) parts.Add(string.Join("\n", lines));
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary><paramref name="text"/> on one line, whitespace runs as one space, cut to <paramref name="max"/> with an ellipsis.</summary>
    public static string OneLine(string text, int max)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
            if (!char.IsWhiteSpace(c) && !char.IsControl(c)) line.Append(c);
            else if (line.Length > 0 && line[^1] != ' ') line.Append(' ');
        var flat = line.ToString();
        return flat.Length <= max ? flat : TextCut.Cut(flat, max - 1) + "…";
    }
}
