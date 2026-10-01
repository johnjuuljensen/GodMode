using System.Text;
using System.Text.Json;
using GodMode.ProjectFiles;
using GodMode.Shared;

namespace GodMode.Server.Services;

/// <summary>
/// The messages held for a session until it can take them (<c>inbox.jsonl</c> in its state folder,
/// <c>.godmode/sessions/{id}/</c>): one JSON line each, oldest first, as <see cref="HeldMessage"/>. Kept on disk
/// whatever the session is doing, so a message is not lost to a stop or a restart; a delivery takes the ones it sent
/// off the front. A line that cannot be read is skipped, and goes with the next delivery's rewrite. The caller holds
/// the session's <see cref="Models.ProjectProcess.InboxLock"/>.
/// </summary>
public static class SessionInbox
{
    public const string FileName = "inbox.jsonl";

    /// <summary>The most characters one message may have: <c>message_parent</c> and the fleet's <c>send</c> refuse a longer one.</summary>
    public const int MaxTextLength = 8000;

    /// <param name="At">When it was held.</param>
    /// <param name="Label">Who it is from, as the receiver reads it: <see cref="LabelOf"/>.</param>
    /// <param name="Text">The message, as its sender wrote it.</param>
    public sealed record HeldMessage(DateTime At, string Label, string Text);

    public static string PathFor(string statePath) => Path.Combine(statePath, FileName);

    public static bool Any(string statePath) => new FileInfo(PathFor(statePath)) is { Exists: true, Length: > 0 };

    /// <summary>The held messages, oldest first; none without the file.</summary>
    public static IReadOnlyList<HeldMessage> Read(string statePath)
    {
        var path = PathFor(statePath);
        if (!File.Exists(path)) return [];
        var messages = new List<HeldMessage>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HeldMessage>(line, JsonDefaults.Compact) is { Label.Length: > 0, Text: not null } message)
                    messages.Add(message);
            }
            catch (JsonException) { }
        }
        return messages;
    }

    /// <summary>Appends <paramref name="message"/>. The state folder must exist: a session without one has nowhere to keep it.</summary>
    public static void Append(string statePath, HeldMessage message)
    {
        if (!Directory.Exists(statePath))
            throw new InvalidOperationException("The session has no state folder to keep the message in.");
        File.AppendAllText(PathFor(statePath), JsonSerializer.Serialize(message, JsonDefaults.Compact) + "\n");
    }

    /// <summary>Takes the first <paramref name="count"/> messages off, as <see cref="Read"/> gave them; the file goes when none is left.</summary>
    public static void RemoveFirst(string statePath, int count)
    {
        if (count <= 0) return;
        var rest = Read(statePath).Skip(count).ToList();
        if (rest.Count == 0)
        {
            File.Delete(PathFor(statePath));
            return;
        }
        AtomicFile.WriteAllText(PathFor(statePath),
            string.Concat(rest.Select(message => JsonSerializer.Serialize(message, JsonDefaults.Compact) + "\n")));
    }

    /// <summary>
    /// The label a message from a session carries, so it cannot pass for the user's own words:
    /// <c>[Message from session {id} "{name}"]</c>, the name on one line.
    /// </summary>
    public static string LabelOf(string sessionId, string name) => $"[Message from session {sessionId} \"{OneLine(name, 100)}\"]";

    /// <summary>The label of a message the fleet's <c>send</c> held from the server's own credential: the user's own overseer.</summary>
    public const string OverseerLabel = "[Message from the overseer on the fleet's endpoint]";

    /// <summary>
    /// Everything held, as one input: each message under its label, oldest first, then each notice on a line of its
    /// own, oldest first. Null when nothing is held.
    /// </summary>
    public static string? Compose(IReadOnlyList<HeldMessage> messages, IEnumerable<string> notices)
    {
        var parts = messages.Select(message => $"{message.Label}\n{message.Text}").ToList();
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
