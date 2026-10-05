using System.Text.RegularExpressions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Commands;

namespace GodMode.Voice;

/// <summary>
/// Dictation (#459): "Diktér til 283" / "Dictate to 283", then the user speaks freely, across pauses, and what they said
/// is sent word for word, as recognized, never by the model. Each final the transcriber commits after a pause is a part;
/// the parts are collected until a terminator: "send" sends them (<see cref="Send"/>), "annullér" or "stop" drops them
/// (<see cref="Cancel"/>). A terminator counts only as a sentence on its own: a final that is the terminator alone, or
/// whose last sentence is ("… Det var det. Send."); never inside a sentence ("… og send den til Peter"). Before it is
/// sent the read-back says how long it is and how it starts, naming the project (<see cref="VoicePhrases.DictationSending"/>).
/// It goes to the session as an answer by voice (<see cref="IGodModeServers.ReplyAsync"/>, ReplyByVoice), marked as
/// transcribed speech (#460). A project that runs, or waits on a permission, or failed to create, is refused up front.
/// While dictating, announcements wait (<see cref="Active"/>, <see cref="HeldAnnouncements"/>): an announcement in a
/// pause to think would interrupt the user's train of thought, and change what is talked about. A dictation abandoned
/// with no terminator (the mic closed, voice stopped, or nothing heard for <see cref="IdleWindow"/>) is dropped, and
/// nothing is sent.
/// </summary>
public sealed partial class Dictation(IGodModeServers servers, ProjectHandles handles, ProjectNames names, VoiceConversation conversation,
    VoicePhrases phrases, TimeProvider? time = null)
{
    /// <summary>What ends a dictation and sends it, as a sentence on its own.</summary>
    public static readonly IReadOnlyList<string> SendWords = ["send", "send det", "send den", "send det nu", "send it", "send now"];

    /// <summary>What ends a dictation and drops it, as a sentence on its own. "Stop" drops it too: the safe reading, nothing sent.</summary>
    public static readonly IReadOnlyList<string> CancelWords =
        ["annullér", "annuller", "annullér det", "annuller det", "afbryd", "stop", "cancel", "cancel it"];

    /// <summary>How many of the dictation's first words the read-back says.</summary>
    public const int WordsReadBack = 8;

    private static readonly IReadOnlyList<string[]> SendTokens = [.. SendWords.Select(CommandResolver.Tokenize)];
    private static readonly IReadOnlyList<string[]> CancelTokens = [.. CancelWords.Select(CommandResolver.Tokenize)];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Taking? _taking;

    /// <summary>How long a dictation waits for more words before it is dropped.</summary>
    public TimeSpan IdleWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Nothing is dictated any more: what was held may be said.</summary>
    public event Action? Released;

    /// <summary>Whether a dictation is being taken.</summary>
    public bool Active
    {
        get
        {
            lock (_lock) return _taking is not null;
        }
    }

    /// <summary>The project dictated to, while a dictation is taken.</summary>
    public ProjectRef? Target
    {
        get
        {
            lock (_lock) return _taking?.Target;
        }
    }

    /// <summary>What has been dictated so far, part by part, as recognized.</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            lock (_lock) return _taking is { } taking ? [.. taking.Parts] : [];
        }
    }

    /// <summary>A dictation being taken: its project, its parts so far, and when words were last heard.</summary>
    private sealed record Taking(ProjectRef Target, List<string> Parts, DateTimeOffset LastHeard);

    /// <summary>A terminator, as a sentence on its own.</summary>
    public enum Terminator { Send, Cancel }

    /// <summary>
    /// Whether <paramref name="text"/> starts a dictation ("Diktér til 283", "Dictate to branch master i GodMode, profil
    /// Mega"): the project said up to the first sentence's end, and what follows it in the same final, its first part. A
    /// project is taken as far as it names one ("Diktér til 283, brug migrationen": 283, then "brug migrationen"). Null
    /// when it is no start: the words are the chat's.
    /// </summary>
    public static (string Reference, string After)? Starts(string text) =>
        Start().Match(text) is { Success: true } match ? (match.Groups["reference"].Value.Trim(), match.Groups["rest"].Value.Trim()) : null;

    /// <summary>
    /// The terminator a final ends with, as a sentence on its own, and the words said before it in that final; no
    /// terminator, and all its words, when it has none.
    /// </summary>
    public static (Terminator? Terminator, string Before) Ends(string text)
    {
        var sentences = Sentences(text);
        if (sentences.Count == 0)
            return (null, "");
        var last = CommandResolver.Tokenize(sentences[^1]);
        Terminator? terminator = SendTokens.Any(t => last.AsSpan().SequenceEqual(t)) ? Terminator.Send
            : CancelTokens.Any(t => last.AsSpan().SequenceEqual(t)) ? Terminator.Cancel
            : null;
        return terminator is null ? (null, text.Trim()) : (terminator, string.Join(" ", sentences.Take(sentences.Count - 1)));
    }

    /// <summary>The text's sentences, as the transcriber punctuated it.</summary>
    public static IReadOnlyList<string> Sentences(string text) =>
        [.. SentenceBreak().Split(text.Trim()).Select(s => s.Trim()).Where(s => CommandResolver.Tokenize(s).Length > 0)];

    /// <summary>
    /// The user said <paramref name="text"/>, a final, while no dictation is taken: if it starts one, what to say (the
    /// project refused, unknown, or the dictation started, its first words taken); null when it is no start.
    /// </summary>
    public async Task<string?> StartAsync(string text, CancellationToken ct)
    {
        if (Starts(text) is not var (said, rest))
            return null;
        if (Resolve(said, rest) is not var (target, more))
            return phrases.DictationUnknown(said);

        conversation.Current = target;
        var name = Named(target);
        ProjectStatus status;
        try { status = await servers.GetStatusAsync(target, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return phrases.DictationNotReached(name, started: false); }
        var refused = status switch
        {
            { CreateFailed: true } => phrases.DictationCreateFailed(name),
            { PendingPermission: { } permission } => phrases.DictationPermission(name, permission.Summary),
            { State: ProjectState.WaitingPermission } => phrases.DictationPermission(name, null),
            { State: ProjectState.Running } => phrases.DictationRunning(name),
            _ => null,
        };
        if (refused is not null)
            return refused;

        lock (_lock) _taking = new Taking(target, [], Now);
        var started = phrases.DictationStarted(name);
        // Words after the project in the same final are its first part; they may end it too ("… Send.")
        return more.Length == 0 ? started : await HearAsync(more, ct) is { } after ? $"{started} {after}" : started;
    }

    /// <summary>
    /// The user said <paramref name="text"/>, a final, while a dictation is taken: a part, said nothing to (null), or a
    /// terminator, and what to say of it.
    /// </summary>
    public async Task<string?> HearAsync(string text, CancellationToken ct)
    {
        var (terminator, before) = Ends(text);
        ProjectRef target;
        lock (_lock)
        {
            if (_taking is not { } taking)
                return null;
            if (before.Length > 0)
                taking.Parts.Add(before);
            _taking = taking with { LastHeard = Now };
            target = taking.Target;
        }
        return terminator switch
        {
            Terminator.Send => await SendAsync(ct),
            Terminator.Cancel => Cancel(target),
            _ => null,
        };
    }

    /// <summary>A tick with nothing heard: a dictation that has waited longer than <see cref="IdleWindow"/> is dropped, and said so.</summary>
    public string? Tick()
    {
        ProjectRef target;
        lock (_lock)
        {
            if (_taking is not { } taking || Now - taking.LastHeard <= IdleWindow)
                return null;
            target = taking.Target;
        }
        End();
        return phrases.DictationDropped(Named(target));
    }

    /// <summary>The dictation is dropped unsent and unsaid (the mic closed, voice stopped).</summary>
    public void Abandon() => End();

    /// <summary>Sends what was dictated, word for word; while it cannot go, it stays, and the user hears why.</summary>
    private async Task<string> SendAsync(CancellationToken ct)
    {
        ProjectRef target;
        string text;
        lock (_lock)
        {
            if (_taking is not { } taking)
                return phrases.DictationEmpty(null);
            target = taking.Target;
            text = string.Join(" ", taking.Parts);
        }
        var name = Named(target);
        if (text.Length == 0)
            return phrases.DictationEmpty(name);

        try
        {
            var status = await servers.GetStatusAsync(target, ct);
            if (status.PendingPermission is { } permission)
                return phrases.DictationHeld(name, phrases.DictationPermission(name, permission.Summary));
            if (status.CreateFailed)
                return phrases.DictationHeld(name, phrases.DictationCreateFailed(name));
            await servers.ReplyAsync(target, text, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return phrases.DictationNotReached(name, started: true);
        }

        End();
        conversation.Current = target;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var start = string.Join(" ", words.Take(WordsReadBack)).TrimEnd('.', ',', ';', ':', '!', '?');
        return phrases.DictationSending(name, Sentences(text).Count, words.Length > WordsReadBack ? $"{start} …" : start);
    }

    private string Cancel(ProjectRef target)
    {
        End();
        return phrases.DictationCancelled(Named(target));
    }

    private SpokenName Named(ProjectRef target) => names.Of(target) ?? new SpokenName(target.ProjectId);

    private void End()
    {
        lock (_lock)
        {
            if (_taking is null) return;
            _taking = null;
        }
        Released?.Invoke();
    }

    /// <summary>
    /// The project said, and what follows it: the longest run of its first words that names one, so a start the
    /// transcriber did not punctuate still finds it ("Diktér til 283 brug migrationen": 283, then "brug migrationen").
    /// </summary>
    private (ProjectRef Target, string After)? Resolve(string said, string rest)
    {
        var words = said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var count = words.Length; count > 0; count--)
        {
            if (handles.Resolve(string.Join(" ", words.Take(count)).TrimEnd(',', ';', ':')) is not { } target)
                continue;
            var after = string.Join(" ", words.Skip(count)).TrimStart(',', ';', ':', ' ');
            return (target, string.Join(" ", new[] { after, rest }.Where(s => s.Length > 0)));
        }
        return null;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    [GeneratedRegex(@"^\s*(?:dikt[eé]r|dikterer|diktere|dictate)\s+(?:til|to)\s+(?<reference>[^.!?…]+?)\s*(?:[.!?…]+\s*(?<rest>.*?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Start();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBreak();
}
