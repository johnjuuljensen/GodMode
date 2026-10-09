using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Commands;

namespace GodMode.Voice;

/// <summary>
/// Dictation (#459): "Diktér til 283" / "Dictate to 283" ("Diktér 283", #529), then the user speaks freely, across pauses, and what they said
/// is sent word for word, as recognized, never by the model. Each final the transcriber commits after a pause is a part;
/// the parts are collected until a terminator, an explicit phrase (<see cref="Phrases"/>): "diktat slut" sends them
/// (<see cref="Send"/>), "annullér diktat" drops them (<see cref="Cancel"/>). A bare "send", "stop" or "annullér" is
/// dictated like any other word (#520): "send" is too likely to be part of the text, and a "stop" that dropped it lost
/// the user's words. A terminator counts only as a sentence on its own: a final that is the phrase alone, or whose last
/// sentence is ("… Det var det. Diktat slut."); never inside a sentence ("… og skriv diktat slut i filen"). Before it is
/// sent the read-back says how long it is and how it starts, naming the project (<see cref="VoicePhrases.DictationSending"/>).
/// It goes to the session as an answer by voice (<see cref="IGodModeServers.ReplyAsync"/>, ReplyByVoice), marked as
/// transcribed speech (#460). A project that runs takes it as any reply, on its stdin, into the turn it is in (#530), and
/// the read-back says it works. A project that waits on a permission, or failed to create, is refused up front; one that
/// has come to since is checked again at the send, and the dictation is kept, said why, rather than lost (#507).
/// While dictating, announcements wait (<see cref="Active"/>, <see cref="HeldAnnouncements"/>): an announcement in a
/// pause to think would interrupt the user's train of thought, and change what is talked about. A dictation abandoned
/// with no terminator (the mic closed, voice stopped, or nothing heard for <see cref="IdleWindow"/>) is dropped, and
/// nothing is sent.
/// </summary>
public sealed partial class Dictation(IGodModeServers servers, ProjectHandles handles, ProjectNames names, VoiceConversation conversation,
    VoicePhrases phrases, TimeProvider? time = null)
{
    /// <summary>
    /// What ends a dictation, as a sentence on its own, and how: the one list of them (#520), and speech recognition's
    /// keyterms (<see cref="GodModeGraph.CommandWords"/>). Two words each, unusual enough never to be dictated; with the
    /// ways the user was heard to say them (#530: "dictate end", "stop dictation", "diktér slut").
    /// </summary>
    public static readonly IReadOnlyList<(string Phrase, Terminator Ends)> Phrases =
    [
        ("diktat slut", Terminator.Send), ("diktér slut", Terminator.Send), ("send diktat", Terminator.Send),
        ("end dictation", Terminator.Send), ("dictation end", Terminator.Send), ("dictate end", Terminator.Send),
        ("send dictation", Terminator.Send), ("stop dictation", Terminator.Send),
        ("annullér diktat", Terminator.Cancel), ("slet diktat", Terminator.Cancel),
        ("cancel dictation", Terminator.Cancel),
    ];

    /// <summary>
    /// Words said in front of a terminator, in its sentence, that leave it a terminator (#530: "Ja, øh, end dictation"):
    /// a hesitation, a yes, an okay. They are dropped with it.
    /// </summary>
    private static readonly FrozenSet<string> Fillers = new[]
    {
        "øh", "øhm", "øhh", "eh", "ehm", "uh", "uhm", "um", "umm", "hm", "hmm", "mm", "er", "erm",
        "ja", "jo", "jah", "nå", "okay", "ok", "yes", "yeah",
    }.Select(Key).ToFrozenSet();

    /// <summary>
    /// The hesitations alone (#530): a sentence of nothing but these right before a terminator ("Øh. Diktat slut.") is
    /// dropped with it. A "ja" said as a sentence of its own may be meant, and is kept.
    /// </summary>
    private static readonly FrozenSet<string> Hesitations = new[]
    {
        "øh", "øhm", "øhh", "eh", "ehm", "uh", "uhm", "um", "umm", "hm", "hmm", "mm", "erm",
    }.Select(Key).ToFrozenSet();

    /// <summary>How many of the dictation's first words the read-back says.</summary>
    public const int WordsReadBack = 8;

    private static readonly FrozenDictionary<string, Terminator> PhraseKeys = Phrases.ToFrozenDictionary(p => Key(p.Phrase), p => p.Ends);

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
    /// project is taken as far as it names one ("Diktér til 283, brug migrationen": 283, then "brug migrationen").
    /// <c>Addressed</c> is whether it was said with "til"/"to": one without ("Diktér FE86", #529) starts a dictation only
    /// when its words name a project. Null when it is no start: the words are the chat's.
    /// </summary>
    public static (string Reference, string After, bool Addressed)? Starts(string text) =>
        Start().Match(text) is { Success: true } match
            ? (match.Groups["reference"].Value.Trim(), match.Groups["rest"].Value.Trim(), match.Groups["to"].Success)
            : null;

    /// <summary>
    /// The terminator a final ends with, as a sentence on its own, and the words said before it in that final; no
    /// terminator, and all its words, when it has none. The phrase is matched as the transcriber may render it: in any
    /// case, punctuated anyhow ("Diktat slut…", "Diktat, slut!"), with or without its accent ("annuller diktat"), a "c"
    /// for a "k" ("dictat slut"), its words run together ("diktatslut") or split by a full stop ("Diktat. Slut."). It
    /// is matched strictly as a sentence: the whole of the final's last one (or last two, split so), never part of one,
    /// but for <see cref="Fillers"/> in front of it ("Ja, øh, diktat slut", #530). Hesitations said as sentences of their
    /// own right before it ("Øh. Diktat slut.") go with it.
    /// </summary>
    public static (Terminator? Terminator, string Before) Ends(string text)
    {
        var sentences = Sentences(text);
        if (sentences.Count == 0)
            return (null, "");
        for (var count = 1; count <= Math.Min(2, sentences.Count); count++)
        {
            var words = Words(string.Join(" ", sentences.TakeLast(count))).SkipWhile(Fillers.Contains);
            if (!PhraseKeys.TryGetValue(string.Concat(words), out var terminator))
                continue;
            var before = sentences.SkipLast(count).ToList();
            while (before.Count > 0 && Words(before[^1]).All(Hesitations.Contains))
                before.RemoveAt(before.Count - 1);
            return (terminator, string.Join(" ", before));
        }
        return (null, text.Trim());
    }

    /// <summary>The text's words, each as a phrase is matched (<see cref="Key"/>); none for a word of punctuation alone.</summary>
    private static IEnumerable<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Key).Where(w => w.Length > 0);

    /// <summary>A phrase as it is matched: its letters and digits alone, in lower case, unaccented, a "c" read as a "k".</summary>
    private static string Key(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
                key.Append(c == 'c' ? 'k' : c);
        }
        return key.ToString();
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
        if (Starts(text) is not var (said, rest, addressed))
            return null;
        if (Resolve(said, rest) is not var (target, more))
            // "Diktér" and words that name no project are the chat's: they may be no start at all
            return addressed ? phrases.DictationUnknown(said) : null;

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
    /// terminator, and what to say of it. A start ("Diktér til 283") is never a part (#530): to the project dictated to,
    /// the dictation goes on, with the words after it; to another, or one no handle names, nothing of it is taken, and
    /// the user hears how to end this one first.
    /// </summary>
    public async Task<string?> HearAsync(string text, CancellationToken ct)
    {
        // A start without "til" that names no project, or is a terminator ("Dictate end", #530), is dictated as said
        if (Starts(text) is not var (said, rest, addressed)
            || (!addressed && (Ends(text).Terminator is not null || Resolve(said, rest) is null)))
            return await HearPartAsync(text, ct);
        ProjectRef target;
        lock (_lock)
        {
            if (_taking is not { } taking)
                return null;
            _taking = taking with { LastHeard = Now };
            target = taking.Target;
        }
        var name = Named(target);
        if (Resolve(said, rest) is not var (other, more) || other != target)
            return phrases.DictationElsewhere(name);
        return more.Length > 0 && await HearPartAsync(more, ct) is { } after ? after : phrases.DictationGoesOn(name);
    }

    /// <summary>A final that is no start, while a dictation is taken: a part, or a terminator and what to say of it.</summary>
    private async Task<string?> HearPartAsync(string text, CancellationToken ct)
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

        bool running;
        try
        {
            var status = await servers.GetStatusAsync(target, ct);
            if (status.PendingPermission is { } permission)
                return phrases.DictationHeld(name, phrases.DictationPermission(name, permission.Summary));
            if (status.CreateFailed)
                return phrases.DictationHeld(name, phrases.DictationCreateFailed(name));
            if (status.State == ProjectState.WaitingPermission)
                return phrases.DictationHeld(name, phrases.DictationPermission(name, null));
            running = status.State == ProjectState.Running;
            await servers.ReplyAsync(target, text, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return phrases.DictationNotReached(name, started: true);
        }

        End();
        conversation.Current = target;
        var sentences = Sentences(text);
        return phrases.DictationSending(name, sentences.Count, Opening(text), running, sentences.LastOrDefault(LooksLikeCommand));
    }

    /// <summary>
    /// How <paramref name="text"/> starts, for the read-back: its first <see cref="WordsReadBack"/> words, or its first
    /// sentence when that is shorter, its last punctuation dropped, with "…" when more follows.
    /// </summary>
    public static string Opening(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        while (count < Math.Min(words.Length, WordsReadBack) && (count == 0 || words[count - 1][^1] is not ('.' or '!' or '?' or '…')))
            count++;
        var start = string.Join(" ", words.Take(count)).TrimEnd('.', ',', ';', ':', '!', '?', '…', ' ');
        return count < words.Length ? $"{start} …" : start;
    }

    /// <summary>
    /// A dictated sentence that looks meant to end the dictation, not to be part of it (#530): a few words, one of them
    /// about dictating ("Dictate end.", "Stop dictating."). The read-back names it.
    /// </summary>
    public static bool LooksLikeCommand(string sentence) =>
        Words(sentence).ToList() is { Count: > 0 and <= 4 } words && words.Any(w => w.StartsWith("dikt", StringComparison.Ordinal));

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

    [GeneratedRegex(@"^\s*(?:dikt[eé]r|dikterer|diktere|dictate)\s+(?:(?<to>til|to)\s+)?(?<reference>[^.!?…]+?)\s*(?:[.!?…]+\s*(?<rest>.*?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Start();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBreak();
}
