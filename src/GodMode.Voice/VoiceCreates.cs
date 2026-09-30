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
/// name and whether there is a prompt; <paramref name="WithPrompt"/> is null for a form with no prompt).
/// </summary>
public sealed record CreateRequest(ServerRoot Root, CreateActionInfo Action, IReadOnlyDictionary<string, string> Inputs, string What,
    string? Issue = null, string? Name = null, bool? WithPrompt = null, bool SeveralServers = false)
{
    /// <summary>What is being made, as one key: the same create confirmed twice is one create.</summary>
    public string Key => string.Join("\n", new[] { Root.ServerId, Root.Profile, Root.Root.Name, Action.Name }
        .Concat(Inputs.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => $"{i.Key}={i.Value}")));
}

/// <summary>A read-back playing, or played: the create it is for, its text, and when it started playing.</summary>
public sealed record ArmedCreate(CreateRequest Request, string ReadBack, DateTimeOffset At);

/// <summary>What a create made: the new session and its handle, or why there is none.</summary>
public sealed record CreateOutcome(CreateRequest Request, ProjectRef? Project, string? Handle, string? Error);

/// <summary>
/// Voice's creates (issue #354). <see cref="Propose"/> works out, from what the user said, which root and action, and
/// fills the action's form (an issue number or key, a name, a prompt): it never guesses a root, and asks back when more
/// than one fits. What it settles on is only read back, in the code's fixed words (<see cref="ReadBackNode"/>,
/// <see cref="VoicePhrases.ReadBack"/>), not the model's. The yes it waits on is to that read-back alone: it is armed
/// when the read-back starts playing (<see cref="Spoken"/>), and any other speech of the bot's drops it, as does
/// <see cref="ConfirmWindow"/> passing. Only a yes said after it started confirms it (<see cref="ConfirmCreateNode"/>).
/// The new session is given its handle at once and announced by it; what the conversation is about stays as it is.
/// </summary>
public sealed partial class SessionCreates(IGodModeServers servers, ProjectHandles handles, TimeProvider? time = null)
{
    /// <summary>How long a failure is when said: the rest is on screen.</summary>
    private const int ErrorSaid = 160;

    private static readonly HashSet<string> IssueWords = new(StringComparer.OrdinalIgnoreCase)
        { "issue", "issues", "sag", "sagen", "case", "ticket", "opgave", "opgaven", "jira" };

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
        { "i", "in", "på", "on", "the", "til", "to", "en", "et", "a", "an", "root", "roden", "profil", "profile", "profilen" };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private CreateRequest? _proposed;
    private (CreateRequest Request, string ReadBack)? _toSay;
    private ArmedCreate? _armed;
    private bool _dropped;
    private Action<CreateOutcome>? _announce;
    private Task _running = Task.CompletedTask;

    /// <summary>How long after its read-back started playing a create waits on the yes.</summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(20);

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The create whose read-back is playing or played, waiting on the user's yes; null once dropped or expired.</summary>
    public ArmedCreate? Armed
    {
        get
        {
            lock (_lock)
            {
                if (_armed is { } armed && Now - armed.At > ConfirmWindow)
                    Drop();
                return _armed;
            }
        }
    }

    /// <summary>The creates confirmed, until each has finished and been announced.</summary>
    public Task Running => Volatile.Read(ref _running);

    /// <summary>From now on, each create's outcome goes to <paramref name="announce"/>.</summary>
    public void Attach(Action<CreateOutcome> announce) => _announce = announce;

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
        lock (_lock) _toSay = (request, readBack.Trim());
    }

    /// <summary>
    /// The bot started saying <paramref name="text"/> (<c>ISessionEventSink.OnResponseAsync</c>): the read-back arms its
    /// create; anything else, an announcement or a reply, drops the create that waits.
    /// </summary>
    public void Spoken(string text)
    {
        lock (_lock)
        {
            if (_toSay is { } toSay && toSay.ReadBack == text.Trim())
            {
                _armed = new ArmedCreate(toSay.Request, toSay.ReadBack, Now);
                _toSay = null;
                _dropped = false;
            }
            else if (_toSay is not null || _armed is not null)
                Drop();
        }
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
    /// said in its place (and the create waits on the yes), or a question back, or why voice cannot create it.
    /// </summary>
    public string Propose(IReadOnlyList<ServerRoot> roots, CreateAsk ask)
    {
        lock (_lock)
        {
            _proposed = null;
            _toSay = null;
            _armed = null;
            _dropped = false;
        }
        var sessionRoots = roots.Where(r => r.Root.Actions?.Any(a => a.Session) == true).ToList();
        if (sessionRoots.Count == 0)
            return "No root on any server can start a session. Nothing was created.";

        var severalServers = roots.Select(r => r.ServerId).Distinct().Count() > 1;
        var root = Clean(ask.Root);
        var matched = root is null ? sessionRoots : Best(sessionRoots, r => [r.Root.Name, r.Profile, r.ServerName], root);
        if (matched.Count == 0)
        {
            var sessionless = root is not null && Best(roots, r => [r.Root.Name, r.Profile], root).Count > 0;
            return sessionless
                ? $"The root '{ask.Root}' has only actions that start no session, which voice does not start yet: tell the user to use the app. Nothing was created."
                : $"Unknown root '{ask.Root}'. Nothing was created. Roots: {Names(sessionRoots, severalServers)}.";
        }

        var issue = Clean(ask.Issue);
        var action = Clean(ask.Action);
        List<(ServerRoot Root, CreateActionInfo Action, Form Form)> candidates = [];
        foreach (var r in matched)
        {
            var actions = r.Root.Actions!.Where(a => a.Session).ToList();
            if (action is not null)
                actions = IssueWords.Contains(action) && Best(actions, a => [a.Name], action).Count == 0
                    ? [.. actions.Where(a => Form.Of(a).HasIssue)]
                    : Best(actions, a => [a.Name], action);
            candidates.AddRange(actions.Select(a => (r, a, Form.Of(a))));
        }
        if (candidates.Count == 0)
            return $"No action '{ask.Action}' in {Names(matched, severalServers)}. Nothing was created. Actions: " +
                string.Join("; ", matched.Select(r => $"{Name(r, severalServers)}: {string.Join(", ", r.Root.Actions!.Where(a => a.Session).Select(a => a.Name))}")) + ".";

        // An issue said fits only an action that takes one of its kind; none said, only one that needs none
        var fitting = candidates.Where(c => c.Form.Fits(issue)).ToList();
        if (fitting.Count == 0)
            return issue is null
                ? $"Every action there needs an issue ({Options(candidates, severalServers)}): ask the user for its number. Nothing was created."
                : $"No action there takes the issue '{ask.Issue}' ({Options(candidates, severalServers)}). Nothing was created.";
        // Of those, the ones voice can fill; those it cannot only when there is nothing else
        var fillable = fitting.Where(c => c.Form.Unfillable.Count == 0).ToList();
        if (fillable.Count > 0) fitting = fillable;

        var rootsLeft = fitting.Select(c => c.Root).Distinct().ToList();
        if (rootsLeft.Count > 1)
            return $"Ambiguous: {rootsLeft.Count} roots fit. Ask the user which, as a closed question: {Names(rootsLeft, severalServers)}. Nothing was created.";
        if (fitting.Count > 1)
            return $"Ambiguous: {Name(rootsLeft[0], severalServers)} has {fitting.Count} actions that fit. Ask the user which: " +
                $"{string.Join(", ", fitting.Select(c => c.Action.Name))}. Nothing was created.";

        var (chosen, chosenAction, form) = fitting[0];
        var where = $"{Name(chosen, severalServers)}, action {chosenAction.Name}";
        if (form.Unfillable.Count > 0)
            return $"{where} needs {string.Join(", ", form.Unfillable)}, which voice cannot fill: tell the user to create it in the app. Nothing was created.";

        var (inputs, missing) = form.Fill(issue, Clean(ask.Name), ask.Prompt?.Trim());
        if (missing.Count > 0)
            return $"{where} needs {string.Join(" and ", missing)}: ask the user for it. Nothing was created yet.";

        var what = form.Describe(inputs);
        var request = new CreateRequest(chosen, chosenAction, inputs, $"{what} in {where}", form.Issue(inputs), form.Name(inputs),
            form.WithPrompt(inputs), severalServers);
        if (_inFlight.ContainsKey(request.Key))
            return $"{what} in {where} is being created already, from an earlier yes: nothing more was done. It is announced when it is done.";
        lock (_lock) _proposed = request;
        return $"Settled: create {what} in {where}. The system reads it back to the user in place of your reply, and waits on " +
            "their yes: respond with one word only, and never say it was created.";
    }

    /// <summary>The create read back is dropped: the user said anything but yes. The one dropped, or null when none waited.</summary>
    public CreateRequest? Cancel()
    {
        lock (_lock)
        {
            var armed = _armed;
            _armed = null;
            _toSay = null;
            return armed?.Request;
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
        }
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
            return new CreateOutcome(request, null, null, message.Length > ErrorSaid ? message[..ErrorSaid] + "…" : message);
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
