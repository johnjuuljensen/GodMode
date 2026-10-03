using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GodMode.Shared.Models;
using VoiceBot.Core.Text;

namespace GodMode.Voice;

/// <summary>What the user asked to start, in their words, as the model gave them: all optional.</summary>
public sealed record CreateAsk(string? Root, string? Action, string? Issue, string? Name, string? Prompt);

/// <summary>
/// A create voice settled on: the root, its action, the form's values, and what the read-back names (the issue, or the
/// name and whether there is a prompt; <paramref name="WithPrompt"/> is null for a form with no prompt), and the prompt
/// itself, which the read-back says when it is short (#449). <paramref name="Label"/> is the issue's label the action was
/// taken from, in place of the one asked for (#473): the read-back says it first ("Issue 471 er mærket epic.").
/// </summary>
public sealed record CreateRequest(ServerRoot Root, CreateActionInfo Action, IReadOnlyDictionary<string, string> Inputs, string What,
    string? Issue = null, string? Name = null, bool? WithPrompt = null, bool SeveralServers = false, string? Prompt = null, string? Label = null)
{
    /// <summary>What is being made, as one key: the same create confirmed twice is one create.</summary>
    public string Key => string.Join("\n", new[] { Root.ServerId, Root.Profile, Root.Root.Name, Action.Name }
        .Concat(Inputs.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => $"{i.Key}={i.Value}")));
}

/// <summary>A read-back playing, or played: the create it is for, its text, and when it started playing.</summary>
public sealed record ArmedCreate(CreateRequest Request, string ReadBack, DateTimeOffset At);

/// <summary>
/// A create whose read-back the user answered with a change ("Nej, som overseer"), not a yes nor a plain no (#449): it
/// waits no more, and goes to the chat with their words, which proposes it again, changed, or says it was cancelled.
/// </summary>
public sealed record CorrectedCreate(CreateRequest Request, string ReadBack)
{
    /// <summary>What the model is told along with the user's words: the create they answer, and what to do with a change.</summary>
    public string Note =>
        $"[The user's words answer the read-back \"{ReadBack}\", of {Request.What}. Nothing was created, and it waits on no yes. " +
        $"If they change it (another action, root, name or prompt), call {VoiceTools.StartSession} again with the change and " +
        "everything else as before: it is read back again. If they only decline it, say it was cancelled.]";
}

/// <summary>What a create made: the new session and its handle, or why there is none.</summary>
public sealed record CreateOutcome(CreateRequest Request, ProjectRef? Project, string? Handle, string? Error);

/// <summary>
/// A create being drafted (#473): the root and action settled on, and what was given for it, as the code holds it
/// between the model's calls. Later calls fill in what it lacks; another root, action or issue takes the user's own
/// words. <paramref name="At"/> is when it was last proposed, which it expires from.
/// </summary>
public sealed record CreateDraft(ServerRoot Root, CreateActionInfo Action, string? Issue, string? Name, string? Prompt, DateTimeOffset At);

/// <summary>
/// Voice's creates (issue #354). <see cref="ProposeAsync"/> works out, from what the user said, which root and action,
/// and fills the action's form (an issue number or key, a name, a prompt): it never guesses a root, and asks back when
/// more than one fits. Once a root and action are settled on, they are a draft held here (#473): the model's later calls
/// only fill in what it lacks, and another root, action or issue is taken only when the user's own latest words name it
/// (<see cref="Heard"/>). An issue's labels are read before it is read back: one labelled as another of the root's issue
/// actions is proposed as that. What it settles on is only read back, in the code's fixed words (<see cref="ReadBackNode"/>,
/// <see cref="VoicePhrases.ReadBack"/>), not the model's. The yes it waits on is to that read-back alone: it is armed
/// when the read-back starts playing (<see cref="Spoken"/>), and <see cref="ConfirmWindow"/> passing drops it. Only a
/// yes said after it started confirms it (<see cref="ConfirmCreateNode"/>). While a create or the draft's question waits
/// on the user (<see cref="Waiting"/>), announcements are held (<see cref="HeldAnnouncements"/>); should other speech
/// come between the read-back and its answer even so, the read-back is said again after it (<see cref="Repeat"/>).
/// The new session is given its handle at once and announced by it; what the conversation is about stays as it is.
/// </summary>
public sealed partial class SessionCreates(IGodModeServers servers, ProjectHandles handles, TimeProvider? time = null)
{
    private static readonly HashSet<string> IssueWords = new(StringComparer.OrdinalIgnoreCase)
        { "issue", "issues", "sag", "sagen", "case", "ticket", "opgave", "opgaven", "jira" };

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
        { "i", "in", "på", "on", "the", "til", "to", "en", "et", "a", "an", "root", "roden", "profil", "profile", "profilen" };

    /// <summary>How often a read-back is said again after other speech came between it and its answer.</summary>
    private const int MaxRepeats = 1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private CreateRequest? _proposed;
    private (CreateRequest Request, string ReadBack, DateTimeOffset At)? _toSay;
    private ArmedCreate? _armed;
    private int _repeats;
    private CorrectedCreate? _corrected;
    private CreateDraft? _draft;
    private bool _asking;
    private string? _heard;
    private bool _dropped;
    private bool _wasWaiting;
    private ITimer? _timer;
    private Action<CreateOutcome>? _announce;
    private Task _running = Task.CompletedTask;

    /// <summary>How long after its read-back started playing a create waits on the yes.</summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a draft is kept after it was last proposed, and its question waits on the user's answer.</summary>
    public TimeSpan DraftWindow { get; init; } = TimeSpan.FromSeconds(30);

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Nothing waits on the user any more (<see cref="Waiting"/>): what was held may be said.</summary>
    public event Action? Released;

    /// <summary>
    /// Other speech came between a read-back and its answer: the read-back, to be said again. It arms its create again
    /// when it starts playing.
    /// </summary>
    public event Action<string>? Repeat;

    /// <summary>The create whose read-back is playing or played, waiting on the user's yes; null once dropped or expired.</summary>
    public ArmedCreate? Armed
    {
        get
        {
            ArmedCreate? armed;
            lock (_lock)
            {
                Expire();
                armed = _armed;
            }
            Notify();
            return armed;
        }
    }

    /// <summary>The draft being filled in, while it lasts.</summary>
    public CreateDraft? Draft
    {
        get
        {
            lock (_lock)
            {
                Expire();
                return _draft;
            }
        }
    }

    /// <summary>
    /// Whether a create or a question waits on the user: one settled on and about to be read back, its read-back said and
    /// unanswered, an answer with a change on its way to the chat, or the draft's question asked and unanswered.
    /// </summary>
    public bool Waiting
    {
        get
        {
            lock (_lock) return WaitingNow();
        }
    }

    private bool WaitingNow()
    {
        Expire();
        return _proposed is not null || _toSay is not null || _armed is not null || _corrected is not null || (_asking && _draft is not null);
    }

    /// <summary>Drops what has waited too long: a read-back unanswered, one never said, and a draft not proposed again.</summary>
    private void Expire()
    {
        var now = Now;
        if (_armed is { } armed && now - armed.At > ConfirmWindow || _toSay is { } toSay && now - toSay.At > ConfirmWindow)
        {
            Drop();
            _draft = null;
        }
        if (_draft is { } draft && now - draft.At > DraftWindow)
        {
            _draft = null;
            _asking = false;
        }
    }

    /// <summary>
    /// Says, once, that nothing waits any more (<see cref="Released"/>), and while something does, looks again when it
    /// would expire.
    /// </summary>
    private void Notify()
    {
        bool released;
        lock (_lock)
        {
            var waiting = WaitingNow();
            released = _wasWaiting && !waiting;
            _wasWaiting = waiting;
            _timer?.Dispose();
            _timer = null;
            if (waiting && Deadline() is { } deadline)
                _timer = _time.CreateTimer(_ => Notify(), null, Max(deadline - Now, TimeSpan.Zero) + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
        }
        if (released) Released?.Invoke();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>When what waits now expires, the soonest.</summary>
    private DateTimeOffset? Deadline()
    {
        DateTimeOffset?[] deadlines =
        [
            _armed?.At + ConfirmWindow,
            _toSay?.At + ConfirmWindow,
            _asking ? _draft?.At + DraftWindow : null,
        ];
        return deadlines.Where(d => d is not null).Min();
    }

    /// <summary>The creates confirmed, until each has finished and been announced.</summary>
    public Task Running => Volatile.Read(ref _running);

    /// <summary>From now on, each create's outcome goes to <paramref name="announce"/>.</summary>
    public void Attach(Action<CreateOutcome> announce) => _announce = announce;

    /// <summary>
    /// The user's latest words, as recognized (<see cref="ReadBackNode"/>), before the chat hears them: the draft's
    /// question is answered, and only these words can change its root, action or issue.
    /// </summary>
    public void Heard(string said)
    {
        lock (_lock)
        {
            _heard = said;
            _asking = false;
        }
    }

    /// <summary>The chat's evaluation is done: whatever it settled, what no longer waits is released.</summary>
    public void Settled() => Notify();

    /// <summary>The create settled on in this evaluation, for its read-back to be said (<see cref="ReadBackNode"/>).</summary>
    public CreateRequest? TakeProposed()
    {
        lock (_lock)
        {
            var proposed = _proposed;
            _proposed = null;
            return proposed;
        }
    }

    /// <summary><paramref name="readBack"/> is what the bot says next, for <paramref name="request"/>: it arms it when it starts playing.</summary>
    public void ReadingBack(CreateRequest request, string readBack)
    {
        lock (_lock)
        {
            _toSay = (request, readBack.Trim(), Now);
            _repeats = 0;
        }
        Notify();
    }

    /// <summary>
    /// The bot started saying <paramref name="text"/> (<c>ISessionEventSink.OnResponseAsync</c>): the read-back arms its
    /// create. Anything else, said while one waits, has the read-back said again after it (<see cref="Repeat"/>), once;
    /// after that it drops the create, and a yes to it is told nothing waits.
    /// </summary>
    public void Spoken(string text)
    {
        string? repeat = null;
        lock (_lock)
        {
            Expire();
            if (_toSay is { } toSay && toSay.ReadBack == text.Trim())
            {
                _armed = new ArmedCreate(toSay.Request, toSay.ReadBack, Now);
                _toSay = null;
                _dropped = false;
            }
            else if (_armed is { } armed && _repeats < MaxRepeats)
            {
                _repeats++;
                _toSay = (armed.Request, armed.ReadBack, Now);
                _armed = null;
                repeat = armed.ReadBack;
            }
            else if (_toSay is not null || _armed is not null)
                Drop();
        }
        if (repeat is not null) Repeat?.Invoke(repeat);
        Notify();
    }

    /// <summary>Whether a create was read back and dropped unanswered since (it expired, or the bot said something else); asking forgets it.</summary>
    public bool TakeDropped()
    {
        lock (_lock)
        {
            var dropped = _dropped;
            _dropped = false;
            return dropped;
        }
    }

    private void Drop()
    {
        _toSay = null;
        _armed = null;
        _dropped = true;
    }

    /// <summary>
    /// What voice makes of <paramref name="ask"/> among <paramref name="roots"/>, for the model: that the read-back is
    /// said in its place (and the create waits on the yes), or a question back, or why voice cannot create it. With a
    /// draft, what it has is kept, and the model's root, action or issue for it only when the user's words name them.
    /// </summary>
    public async Task<string> ProposeAsync(IReadOnlyList<ServerRoot> roots, CreateAsk ask, CancellationToken ct)
    {
        CreateDraft? draft;
        string? heard;
        lock (_lock)
        {
            _proposed = null;
            _toSay = null;
            _armed = null;
            _corrected = null;
            _dropped = false;
            _asking = false;
            Expire();
            draft = _draft;
            heard = _heard;
        }
        var (said, request, settled) = await SettleAsync(roots, ask, draft, heard, ct);
        lock (_lock)
        {
            _draft = settled;
            // Asked back about the draft: the user's answer is waited on
            _asking = settled is not null && request is null;
            _proposed = request;
        }
        Notify();
        return said;
    }

    /// <summary>What to tell the model, the create settled on (null for none yet), and the draft from now on (null for none).</summary>
    private async Task<(string Said, CreateRequest? Request, CreateDraft? Draft)> SettleAsync(IReadOnlyList<ServerRoot> roots, CreateAsk ask,
        CreateDraft? draft, string? heard, CancellationToken ct)
    {
        var sessionRoots = roots.Where(r => r.Root.Actions?.Any(a => a.Session) == true).ToList();
        if (sessionRoots.Count == 0)
            return ("No root on any server can start a session. Nothing was created.", null, null);

        var severalServers = roots.Select(r => r.ServerId).Distinct().Count() > 1;
        var root = Clean(ask.Root);
        var action = Clean(ask.Action);
        var issue = Clean(ask.Issue);
        var name = Clean(ask.Name);
        var prompt = ask.Prompt?.Trim();
        List<string> kept = [];
        ServerRoot? draftRoot = null;
        CreateActionInfo? draftAction = null;
        // The draft, as the code holds it: the model fills in what it lacks, and changes what it has only on the user's words
        if (draft is not null && sessionRoots.FirstOrDefault(r => Same(r, draft.Root)) is { } heldRoot)
        {
            var otherRoot = root is not null && Best([heldRoot], r => [r.Root.Name, r.Profile, r.ServerName], root).Count == 0;
            if (otherRoot && !Says(heard, Best(sessionRoots, r => [r.Root.Name, r.Profile], root!).SelectMany(r => new[] { r.Root.Name, r.Profile })))
            {
                kept.Add($"the root stays {Name(heldRoot, severalServers)}: the user did not name '{root}'");
                otherRoot = false;
            }
            if (!otherRoot)
            {
                draftRoot = heldRoot;
                var heldAction = heldRoot.Root.Actions!.FirstOrDefault(a => a.Session && a.Name == draft.Action.Name);
                var otherAction = action is not null && heldAction is not null && !Names(heldRoot, heldAction, action);
                if (otherAction && !SaysAction(heard, heldRoot, action!))
                {
                    kept.Add($"the action stays {heldAction!.Name}: the user did not ask for '{action}'");
                    otherAction = false;
                }
                if (!otherAction && heldAction is not null)
                {
                    draftAction = heldAction;
                    if (issue is null) issue = draft.Issue;
                    else if (draft.Issue is not null && !SameIssue(issue, draft.Issue) && !SaysIssue(heard, issue))
                    {
                        kept.Add($"the issue stays {draft.Issue}: the user did not say '{issue}'");
                        issue = draft.Issue;
                    }
                }
            }
            name ??= draft.Name;
            prompt = prompt is { Length: > 0 } ? prompt : draft.Prompt;
        }
        var keptNote = kept.Count == 0 ? "" : $"Kept from the draft, as the user has not changed it: {string.Join("; ", kept)}. ";

        List<ServerRoot> matched = draftRoot is not null ? [draftRoot]
            : root is null ? sessionRoots
            : Best(sessionRoots, r => [r.Root.Name, r.Profile, r.ServerName], root);
        if (matched.Count == 0)
        {
            var sessionless = root is not null && Best(roots, r => [r.Root.Name, r.Profile], root).Count > 0;
            return (sessionless
                ? $"The root '{ask.Root}' has only actions that start no session, which voice does not start yet: tell the user to use the app. Nothing was created."
                : $"Unknown root '{ask.Root}'. Nothing was created. Roots: {Names(sessionRoots, severalServers)}.", null, null);
        }

        List<(ServerRoot Root, CreateActionInfo Action, Form Form)> candidates = [];
        foreach (var r in matched)
        {
            var actions = r.Root.Actions!.Where(a => a.Session).ToList();
            if (draftAction is not null)
                actions = [draftAction];
            else if (action is not null)
                actions = IssueWords.Contains(action) && Best(actions, a => [a.Name], action).Count == 0
                    ? [.. actions.Where(a => Form.Of(a).HasIssue)]
                    : Best(actions, a => [a.Name], action);
            candidates.AddRange(actions.Select(a => (r, a, Form.Of(a))));
        }
        if (candidates.Count == 0)
            return ($"No action '{ask.Action}' in {Names(matched, severalServers)}. Nothing was created. Actions: " +
                string.Join("; ", matched.Select(r => $"{Name(r, severalServers)}: {string.Join(", ", r.Root.Actions!.Where(a => a.Session).Select(a => a.Name))}")) + ".",
                null, null);

        // An issue said fits only an action that takes one of its kind; none said, only one that needs none
        var fitting = candidates.Where(c => c.Form.Fits(issue)).ToList();
        if (fitting.Count == 0)
            return issue is null
                // One root and action: the number asked for is asked for it, and kept for the next call
                ? ($"{keptNote}Every action there needs an issue ({Options(candidates, severalServers)}): ask the user for its number, then call " +
                    $"{StartSession} again with it. Nothing was created.", null,
                    candidates is [var only] ? new CreateDraft(only.Root, only.Action, issue, name, prompt, Now) : null)
                : ($"{keptNote}No action there takes the issue '{issue}' ({Options(candidates, severalServers)}). Nothing was created.", null, null);
        // Of those, the ones voice can fill; those it cannot only when there is nothing else
        var fillable = fitting.Where(c => c.Form.Unfillable.Count == 0).ToList();
        if (fillable.Count > 0) fitting = fillable;

        var rootsLeft = fitting.Select(c => c.Root).Distinct().ToList();
        if (rootsLeft.Count > 1)
            return ($"Ambiguous: {rootsLeft.Count} roots fit. Ask the user which, as a closed question: {Names(rootsLeft, severalServers)}. Nothing was created.", null, null);
        if (fitting.Count > 1)
            return ($"Ambiguous: {Name(rootsLeft[0], severalServers)} has {fitting.Count} actions that fit. Ask the user which: " +
                $"{string.Join(", ", fitting.Select(c => c.Action.Name))}. Nothing was created.", null, null);

        var (chosen, chosenAction, form) = fitting[0];
        var asked = chosenAction.Name;
        // The issue's labels: one that names another of the root's issue actions is started as that (#473)
        string? label = null;
        if (issue is not null && form.HasIssue && await LabelledAsync(chosen, issue, ct) is { } labels)
        {
            var labelled = chosen.Root.Actions!.Where(a => a.Session && Form.Of(a).Fits(issue) && labels.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (labelled.Count > 0 && !labelled.Contains(chosenAction))
            {
                if (labelled.Count > 1)
                    return ($"{keptNote}Issue {issue} is labelled {string.Join(" and ", labelled.Select(a => a.Name))}, not {asked}: ask the user " +
                        "which to start it as. Nothing was created.", null, new CreateDraft(chosen, chosenAction, issue, name, prompt, Now));
                label = labelled[0].Name;
                (chosenAction, form) = (labelled[0], Form.Of(labelled[0]));
            }
        }

        var where = $"{Name(chosen, severalServers)}, action {chosenAction.Name}";
        var labelNote = label is null ? "" : $"Issue {issue} is labelled {label}, so it is started as {label}, not {asked}: the read-back says so. ";
        if (form.Unfillable.Count > 0)
            return ($"{where} needs {string.Join(", ", form.Unfillable)}, which voice cannot fill: tell the user to create it in the app. Nothing was created.", null, null);

        var newDraft = new CreateDraft(chosen, chosenAction, issue, name, prompt, Now);
        var (inputs, missing) = form.Fill(issue, name, prompt);
        // The action the user named stays: what it lacks is asked for, and never made up by starting another (#449)
        if (missing.Count > 0)
            return ($"{keptNote}{labelNote}{where} needs {string.Join(" and ", missing)}: ask the user for it, then call {StartSession} again " +
                $"with their answer. The draft keeps action '{chosenAction.Name}' and the root: never start another in its place, " +
                "nor put its name in another's name. Nothing was created yet.", null, newDraft);

        var what = form.Describe(inputs);
        var request = new CreateRequest(chosen, chosenAction, inputs, $"{what} in {where}", form.Issue(inputs), form.Name(inputs),
            form.WithPrompt(inputs), severalServers, form.Prompt(inputs), label);
        if (_inFlight.ContainsKey(request.Key))
            return ($"{what} in {where} is being created already, from an earlier yes: nothing more was done. It is announced when it is done.", null, null);
        return ($"{keptNote}{labelNote}Settled: create {what} in {where}. The system reads it back to the user in place of your reply, and waits on " +
            "their yes: respond with one word only, and never say it was created.", request, newDraft);
    }

    private const string StartSession = VoiceTools.StartSession;

    /// <summary>The issue's labels, from its root's issueInfo script; null when it has none, or it could not say.</summary>
    private async Task<IReadOnlyList<string>?> LabelledAsync(ServerRoot root, string issue, CancellationToken ct)
    {
        try
        {
            return (await servers.DescribeIssueAsync(root, Form.Normal(issue), ct))?.Labels;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An older server, or a script that failed: the create is read back unchecked, and its create script is the backstop
            return null;
        }
    }

    private static bool Same(ServerRoot a, ServerRoot b) =>
        a.ServerId == b.ServerId && a.Profile == b.Profile && a.Root.Name == b.Root.Name;

    /// <summary>Whether <paramref name="spoken"/> names <paramref name="action"/>: as itself, or an issue word for one that takes an issue, in a root with no action named so.</summary>
    private static bool Names(ServerRoot root, CreateActionInfo action, string spoken) =>
        Best([action], a => [a.Name], spoken).Count > 0
        || IssueWords.Contains(spoken) && Form.Of(action).HasIssue && Best(root.Root.Actions!.Where(a => a.Session), a => [a.Name], spoken).Count == 0;

    /// <summary>
    /// Whether the user's words name the action <paramref name="spoken"/> stands for in <paramref name="root"/>: one of
    /// its words ("Nej, som overseer"), or, for an issue word, an issue word ("start it as an issue").
    /// </summary>
    private static bool SaysAction(string? heard, ServerRoot root, string spoken)
    {
        if (heard is null) return false;
        var meant = Best(root.Root.Actions!.Where(a => a.Session), a => [a.Name], spoken);
        return Says(heard, meant.Select(a => a.Name))
            || IssueWords.Contains(spoken) && Words(heard).Any(IssueWords.Contains);
    }

    /// <summary>Whether a word of <paramref name="heard"/>, or two said together ("god mode"), is one of <paramref name="names"/> or a word of one.</summary>
    private static bool Says(string? heard, IEnumerable<string> names)
    {
        if (heard is null) return false;
        var words = Words(heard).Where(w => !Filler.Contains(w)).ToList();
        var said = words.Concat(words.Zip(words.Skip(1), (a, b) => a + b)).ToList();
        var keys = names.Where(n => n.Length > 0).SelectMany(n => Words(n).Append(n)).Select(Key).Where(k => k.Length > 0).ToHashSet();
        return said.Any(w => keys.Contains(Key(w)) || keys.Any(k => Close(k, w)));
    }

    /// <summary>Whether the user's words hold the issue <paramref name="issue"/>: its number or key, as digits or as words.</summary>
    private static bool SaysIssue(string? heard, string issue)
    {
        if (heard is null) return false;
        var key = Key(Form.Normal(issue));
        var digits = new string([.. key.Where(char.IsAsciiDigit)]);
        return key.Length > 0 && Key(heard).Contains(key, StringComparison.Ordinal)
            || digits.Length > 0 && SpokenDigits(heard).Contains(digits, StringComparison.Ordinal);
    }

    private static bool SameIssue(string a, string b) => Key(Form.Normal(a)) == Key(Form.Normal(b));

    private static readonly Dictionary<string, char> DigitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["four"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7',
        ["eight"] = '8', ["nine"] = '9', ["nul"] = '0', ["en"] = '1', ["et"] = '1', ["to"] = '2', ["tre"] = '3', ["fire"] = '4', ["fem"] = '5',
        ["seks"] = '6', ["syv"] = '7', ["otte"] = '8', ["ni"] = '9',
    };

    /// <summary>The digits said: as digits, one by one in words ("four, seven, one"), or as a whole number in Danish words.</summary>
    private static string SpokenDigits(string heard) =>
        DanishNumbers.Parse(heard) is { } number ? number.ToString()
            : new string([.. Words(heard).SelectMany<string, char>(w => w.All(char.IsAsciiDigit) ? w : DigitWords.TryGetValue(w, out var d) ? [d] : "")]);

    /// <summary>The create read back is dropped: the user said anything but yes. The one dropped, or null when none waited.</summary>
    public CreateRequest? Cancel()
    {
        CreateRequest? cancelled;
        lock (_lock)
        {
            cancelled = _armed?.Request;
            _armed = null;
            _toSay = null;
            // A plain no ends the draft too: what comes next is a new ask
            _draft = null;
        }
        Notify();
        return cancelled;
    }

    /// <summary>
    /// The create read back is answered with a change (<see cref="ConfirmCreateNode"/>): it waits no more, and is kept
    /// for the chat (<see cref="TakeCorrected"/>). Its draft stays, for the change the user's words name. The one
    /// corrected, or null when none waited.
    /// </summary>
    public CorrectedCreate? Correct()
    {
        CorrectedCreate? corrected;
        lock (_lock)
        {
            var armed = _armed;
            _armed = null;
            _toSay = null;
            _corrected = corrected = armed is null ? null : new CorrectedCreate(armed.Request, armed.ReadBack);
            if (_draft is not null) _draft = _draft with { At = Now };
        }
        Notify();
        return corrected;
    }

    /// <summary>The create corrected in this evaluation, for the chat to hear of (<see cref="ReadBackNode"/>); taking it forgets it.</summary>
    public CorrectedCreate? TakeCorrected()
    {
        lock (_lock)
        {
            var corrected = _corrected;
            _corrected = null;
            return corrected;
        }
    }

    /// <summary>
    /// The user said yes to <paramref name="armed"/>: its create starts, and its outcome is announced when it is done.
    /// Null when it no longer waits (dropped, or read back again since).
    /// </summary>
    public CreateRequest? Confirm(ArmedCreate armed)
    {
        lock (_lock)
        {
            if (_armed != armed)
                return null;
            _armed = null;
            _draft = null;
        }
        Notify();
        var request = armed.Request;
        if (!_inFlight.TryAdd(request.Key, 0))
            return request;
        var previous = Running;
        Volatile.Write(ref _running, Task.Run(async () =>
        {
            await previous;
            try
            {
                var outcome = await CreateAsync(request);
                _announce?.Invoke(outcome);
            }
            finally
            {
                _inFlight.TryRemove(request.Key, out _);
            }
        }));
        return request;
    }

    private async Task<CreateOutcome> CreateAsync(CreateRequest request)
    {
        try
        {
            var result = await servers.CreateAsync(request.Root, request.Action.Name, request.Inputs, CancellationToken.None);
            if (result.Project is not { } status)
                return new CreateOutcome(request, null, null, null);
            var project = new ProjectRef(request.Root.ServerId, status.Id);
            // As the project board names it when the hub pushes it: the same handle, whichever comes first
            // What the conversation is about stays as it is: a slow create does not take a later answer to itself
            var handle = handles.For(project, status.Name, status.RootName, status.Kind);
            return new CreateOutcome(request, project, handle, null);
        }
        catch (Exception ex)
        {
            var message = Regex.Replace(ex.Message, @"^An unexpected error occurred invoking '\w+' on the server\.\s*(HubException:\s*)?", "");
            // Whole: what is said of it is short (VoicePhrases.Created), and the server's log has it all
            return new CreateOutcome(request, null, null, message.Trim());
        }
    }

    private static string Names(IEnumerable<ServerRoot> roots, bool severalServers) =>
        string.Join(", ", roots.Select(r => Name(r, severalServers)));

    /// <summary>"GodMode (profile Godmode)", with its server when there are several.</summary>
    private static string Name(ServerRoot root, bool severalServers) =>
        $"{root.Root.Name} (profile {root.Profile}{(severalServers ? $", server {root.ServerName}" : "")})";

    private static string Options(IEnumerable<(ServerRoot Root, CreateActionInfo Action, Form Form)> candidates, bool severalServers) =>
        string.Join("; ", candidates.Select(c => $"{Name(c.Root, severalServers)}: {c.Action.Name}"));

    /// <summary>
    /// Those whose names match <paramref name="spoken"/> best: said as one of them ("GodMode", "god mode"), or each word
    /// of it as one of them or a word of one ("Assistant Outbound", "api"); else the same misheard slightly
    /// ("Assistenten"). Never one that only resembles it less than the rest: a tie is ambiguous, and asked about.
    /// </summary>
    private static List<T> Best<T>(IEnumerable<T> items, Func<T, IEnumerable<string>> names, string spoken)
    {
        var words = Words(spoken).Where(w => !Filler.Contains(w)).ToList();
        if (words.Count == 0) return [];
        var scored = items.Select(i => (Item: i, Score: Score([.. names(i).Where(n => n.Length > 0)], words))).Where(s => s.Score > 0).ToList();
        return scored.Count == 0 ? [] : [.. scored.Where(s => s.Score == scored.Max(m => m.Score)).Select(s => s.Item)];
    }

    /// <summary>2: said exactly; 1: said close to it; 0: not said.</summary>
    private static int Score(IReadOnlyList<string> names, IReadOnlyList<string> words)
    {
        var whole = Key(string.Concat(words));
        if (names.Any(n => Key(n) == whole) || words.All(w => names.Any(n => Key(n) == Key(w) || Words(n).Any(p => Key(p) == Key(w)))))
            return 2;
        return names.Any(n => Close(n, whole)) || words.All(w => names.Any(n => Close(n, w) || Words(n).Any(p => Close(p, w)))) ? 1 : 0;
    }

    /// <summary>Misheard slightly, or said with a Danish definite ending ("assistenten", "sagen").</summary>
    private static bool Close(string name, string spoken)
    {
        var key = Key(name);
        return key.Length >= 3 && Stems(Key(spoken)).Any(s => s.Length >= 3 && FuzzyMatch.JaroWinkler(key, s) >= 0.9);
    }

    private static IEnumerable<string> Stems(string key)
    {
        yield return key;
        foreach (var ending in new[] { "erne", "en", "et", "n" })
            if (key.Length > ending.Length + 3 && key.EndsWith(ending, StringComparison.Ordinal))
                yield return key[..^ending.Length];
    }

    private static string Key(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static IEnumerable<string> Words(string text) => WordPattern().Matches(text).Select(m => m.Value);

    private static string? Clean(string? spoken) =>
        spoken?.Trim().Trim('.', ',', '!', '?', '"', '\'', ' ') is { Length: > 0 } text ? text : null;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    /// <summary>An action's form, as voice sees it: the fields it can fill, and those it cannot.</summary>
    private sealed partial record Form(IReadOnlyList<Field> Fields)
    {
        public bool HasIssue => Fields.Any(f => f.Role is Role.IssueNumber or Role.IssueKey);

        /// <summary>The required fields voice has no words for, by their titles.</summary>
        public IReadOnlyList<string> Unfillable => [.. Fields.Where(f => f.Required && f.Role == Role.Other).Select(f => f.Title)];

        /// <summary>An issue said fits a form with a field for it of its kind; none said, a form that requires none.</summary>
        public bool Fits(string? issue) =>
            issue is null
                ? !Fields.Any(f => f.Required && f.Role is Role.IssueNumber or Role.IssueKey)
                : Fields.Any(f => Value(f.Role, issue) is not null);

        public (IReadOnlyDictionary<string, string> Inputs, IReadOnlyList<string> Missing) Fill(string? issue, string? name, string? prompt)
        {
            Dictionary<string, string> inputs = [];
            List<string> missing = [];
            foreach (var field in Fields)
            {
                var value = field.Role switch
                {
                    Role.IssueNumber or Role.IssueKey when issue is not null => Value(field.Role, issue),
                    Role.Name => name,
                    Role.Prompt => prompt is { Length: > 0 } ? prompt : null,
                    _ => null,
                };
                if (value is not null) inputs[field.Key] = value;
                else if (field.Required) missing.Add(field.Title);
            }
            return (inputs, missing);
        }

        public string? Issue(IReadOnlyDictionary<string, string> inputs) => ValueOf(inputs, Role.IssueNumber, Role.IssueKey);

        public string? Name(IReadOnlyDictionary<string, string> inputs) => ValueOf(inputs, Role.Name);

        public string? Prompt(IReadOnlyDictionary<string, string> inputs) => ValueOf(inputs, Role.Prompt);

        /// <summary>Whether a prompt is given; null for a form with no prompt.</summary>
        public bool? WithPrompt(IReadOnlyDictionary<string, string> inputs) =>
            Fields.FirstOrDefault(f => f.Role == Role.Prompt) is { } prompt ? inputs.ContainsKey(prompt.Key) : null;

        private string? ValueOf(IReadOnlyDictionary<string, string> inputs, params Role[] roles) =>
            Fields.Where(f => roles.Contains(f.Role)).Select(f => inputs.GetValueOrDefault(f.Key)).FirstOrDefault(v => v is not null);

        /// <summary>"issue 283", "the name 'backup job' and the prompt '…'", "the name 'backup job', with no prompt".</summary>
        public string Describe(IReadOnlyDictionary<string, string> inputs)
        {
            var parts = new List<string>();
            foreach (var field in Fields)
            {
                if (!inputs.TryGetValue(field.Key, out var value)) continue;
                parts.Add(field.Role switch
                {
                    Role.IssueNumber or Role.IssueKey => $"issue {value} (its title is not known here: read back the number)",
                    Role.Name => $"a session named '{value}'",
                    Role.Prompt => $"the prompt '{value}'",
                    _ => $"{field.Title} '{value}'",
                });
            }
            if (Fields.Any(f => f.Role == Role.Prompt && !inputs.ContainsKey(f.Key)))
                parts.Add("no prompt (it starts idle, waiting for the first message)");
            return parts.Count == 0 ? "a session" : string.Join(", ", parts);
        }

        /// <summary>An issue as its field takes it: a number's digits, a key's "BD-123", else as said.</summary>
        public static string Normal(string issue) => Value(Role.IssueNumber, issue) ?? Value(Role.IssueKey, issue) ?? issue.Trim();

        /// <summary>An issue said as its field takes it: a number ("283", "#283", "to hundrede og treogfirs"), or a key ("BD-123", "bd 123").</summary>
        private static string? Value(Role role, string issue) => role switch
        {
            Role.IssueNumber => DanishNumbers.Parse(Lead().Replace(issue, "").TrimStart('#')) is { } number ? number.ToString() : null,
            Role.IssueKey => IssueKey().Match(Lead().Replace(issue, "")) is { Success: true } key
                ? $"{key.Groups[1].Value.ToUpperInvariant()}-{key.Groups[2].Value}"
                : null,
            _ => null,
        };

        public static Form Of(CreateActionInfo action)
        {
            if (action.InputSchema is not { ValueKind: JsonValueKind.Object } schema
                || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return new Form([new Field("name", "Name", true, Role.Name), new Field("prompt", "Prompt", false, Role.Prompt)]);

            var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            return new Form([.. properties.EnumerateObject().Select(p => new Field(p.Name,
                p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t ? t : p.Name,
                required.Contains(p.Name), RoleOf(p.Name)))]);
        }

        private static Role RoleOf(string key) => key.ToLowerInvariant() switch
        {
            "name" => Role.Name,
            "prompt" => Role.Prompt,
            var k when k.Contains("issue") => k.Contains("key") ? Role.IssueKey : Role.IssueNumber,
            _ => Role.Other,
        };

        [GeneratedRegex(@"^\s*(issue|sag|case|nummer|number|nr\.?)\s+", RegexOptions.IgnoreCase)]
        private static partial Regex Lead();

        [GeneratedRegex(@"^\s*([A-Za-z]+)[\s-]*(\d+)\s*$")]
        private static partial Regex IssueKey();
    }

    private sealed record Field(string Key, string Title, bool Required, Role Role);

    private enum Role { Other, IssueNumber, IssueKey, Name, Prompt }
}
