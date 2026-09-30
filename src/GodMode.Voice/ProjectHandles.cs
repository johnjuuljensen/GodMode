using System.Text.RegularExpressions;
using VoiceBot.Core.Text;

namespace GodMode.Voice;

/// <summary>A project on one of the servers: the server's ID and the project's, both as they were given.</summary>
public sealed record ProjectRef(string ServerId, string ProjectId)
{
    /// <summary>One string for both, as an announcement's source.</summary>
    public string Key => $"{ServerId}\n{ProjectId}";

    public static ProjectRef? FromKey(string? key) =>
        key?.Split('\n') is [var server, var project] ? new ProjectRef(server, project) : null;
}

/// <summary>
/// Short spoken names for projects: the issue number in the project's name ("283"), else a distinctive word of it
/// ("vonage", "testing"), else its kind ("chat"); when that is taken too, the first of them with the lowest free
/// number after it ("chat 2"). A project keeps its handle while it exists, and no two projects have one, across
/// servers. A project that is gone is forgotten (<see cref="Forget"/>), but its handle is retired, not freed: for the
/// rest of the session it names nothing and is given to no other project, so a handle said from an earlier list never
/// reaches another project. Only the same session coming back under a new ID (its profile renamed) gets it again.
/// </summary>
public sealed partial class ProjectHandles
{
    /// <summary>ElevenLabs takes keyterms of at most 20 characters; a handle is one.</summary>
    public const int MaxLength = 20;

    /// <summary>Words that tell projects apart poorly: branch prefixes and filler.</summary>
    private static readonly HashSet<string> Undistinctive = new(StringComparer.OrdinalIgnoreCase)
    {
        "feat", "feature", "fix", "bug", "bugfix", "epic", "issue", "chore", "docs", "the", "and", "for", "with",
        "from", "into", "add", "new", "og", "til", "med", "for", "der", "det", "den", "som", "på",
    };

    private readonly Lock _lock = new();
    private readonly Dictionary<ProjectRef, Entry> _byProject = [];
    private readonly Dictionary<string, ProjectRef> _byHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ProjectRef Project, Entry Entry)> _retired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a handle was given for: the project's name, and its root and kind once known.</summary>
    private sealed record Entry(string Handle, string Name, string? Root, string? Kind);

    /// <summary>Every handle given so far, in the order they were given.</summary>
    public IReadOnlyList<string> All
    {
        get { lock (_lock) return [.. _byHandle.Keys]; }
    }

    /// <summary>
    /// The project's handle, given now if it has none yet. Its <paramref name="root"/> and <paramref name="kind"/>
    /// (<see cref="GodMode.Shared.Models.ProjectSummary.Kind"/>) name it too, when no other project has them; given
    /// later, they are kept, but the handle stays.
    /// </summary>
    public string For(ProjectRef project, string name, string? root = null, string? kind = null)
    {
        lock (_lock)
        {
            if (_byProject.TryGetValue(project, out var known))
            {
                if ((known.Root is null && root is not null) || (known.Kind is null && kind is not null))
                    _byProject[project] = known with { Root = known.Root ?? root, Kind = known.Kind ?? kind };
                return known.Handle;
            }

            var handle = Returning(project, root)
                ?? Candidates(name, kind).FirstOrDefault(Free) ?? Numbered(name, kind);
            _retired.Remove(handle);
            _byProject[project] = new Entry(handle, name, root, kind);
            _byHandle[handle] = project;
            return handle;
        }
    }

    /// <summary>The handle given to the project, or null when it has none.</summary>
    public string? Of(ProjectRef project)
    {
        lock (_lock) return _byProject.TryGetValue(project, out var known) ? known.Handle : null;
    }

    /// <summary>The project is gone: it names nothing any more, and its handle is retired.</summary>
    public void Forget(ProjectRef project)
    {
        lock (_lock)
        {
            if (!_byProject.Remove(project, out var known)) return;
            _byHandle.Remove(known.Handle);
            _retired[known.Handle] = (project, known);
        }
    }

    /// <summary>Whether the reference is the handle of a project that is gone ("chat 2 was deleted").</summary>
    public bool IsRetired(string spoken)
    {
        var reference = Clean(spoken);
        lock (_lock) return Retired(reference);
    }

    /// <summary>
    /// The project a spoken reference names: a handle, a number said in digits or Danish words ("to hundrede og
    /// treogfirs"), a word of a project's name that only one project has, the root or kind of only one project
    /// ("Assistant", "chat"), or, with <paramref name="fuzzy"/>, a handle or such a root misheard slightly
    /// ("Assistent"). A number is never matched fuzzily: "28" is not "283". Null when it names none, or more than one.
    /// </summary>
    public ProjectRef? Resolve(string spoken, bool fuzzy = true)
    {
        var reference = Clean(spoken);
        if (reference.Length == 0) return null;

        lock (_lock)
        {
            if (DanishNumbers.Parse(reference) is { } number && _byHandle.TryGetValue(number.ToString(), out var byNumber))
                return byNumber;
            if (_byHandle.TryGetValue(reference, out var byHandle))
                return byHandle;
            // A numbered handle said with its number in words ("chat to") is that handle; one no project has, or of a
            // project that is gone, names none: never the project with its stem ("chat"), nor one close to it ("chat 3")
            if (NumberedForm(reference) is { } numbered)
                return _byHandle.GetValueOrDefault(numbered);
            if (Retired(reference))
                return null;

            var words = Words(reference).ToList();
            var named = _byProject
                .Where(p => words.Any(w => w.Equals(p.Value.Handle, StringComparison.OrdinalIgnoreCase))
                    || Words(p.Value.Name).Any(n => n.Equals(reference, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Key)
                .ToList();
            if (named.Count == 1) return named[0];
            if (named.Count > 1) return null;

            // A root or kind that only one project has names it; one that several have names none
            var grouped = _byProject.Where(p => Same(p.Value.Root, reference) || Same(p.Value.Kind, reference)).Select(p => p.Key).ToList();
            if (grouped.Count > 0) return grouped.Count == 1 ? grouped[0] : null;
            if (!fuzzy || DanishNumbers.Parse(reference) is not null) return null;

            var close = _byHandle
                .Select(h => (h.Value, Score: Closeness(h.Key, reference)))
                .Where(h => h.Score >= 0.9)
                .OrderByDescending(h => h.Score)
                .ToList();
            if (close.Count == 1 || (close.Count > 1 && close[0].Score > close[1].Score)) return close[0].Value;
            if (close.Count > 1) return null;

            // A root misheard ("Assistent" for Assistant): only when one project is in the roots it is close to
            var roots = _byProject.Where(p => p.Value.Root is { } root && Closeness(root, reference) >= 0.9).Select(p => p.Key).ToList();
            return roots is [var only] ? only : null;
        }
    }

    private bool Retired(string reference) =>
        _retired.ContainsKey(NumberedForm(reference) ?? reference)
        || (DanishNumbers.Parse(reference) is { } number && _retired.ContainsKey(number.ToString()));

    /// <summary>
    /// "stem n" for a stem some handle has, live or retired ("chat" of "chat", "chat 3"): words, then a number in
    /// digits or Danish words, as <see cref="Numbered"/> makes them. It is the handle as <see cref="Numbered"/> writes
    /// it ("chat to" is "chat 2"), or null when the reference is none: a number as a whole (that is looked up as one),
    /// or with a stem no handle has ("issue 283", left to the other steps).
    /// </summary>
    private string? NumberedForm(string reference)
    {
        if (DanishNumbers.Parse(reference) is not null) return null;
        var words = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var tail = 1; tail < words.Length; tail++)
        {
            var stem = string.Join(' ', words[..^tail]);
            if (DanishNumbers.Parse(string.Join(' ', words[^tail..])) is { } number && DanishNumbers.Parse(stem) is null
                && _byHandle.Keys.Concat(_retired.Keys).Any(h => Same(StemOf(h), stem)))
                return $"{stem} {number}";
        }
        return null;
    }

    /// <summary>A handle without the number <see cref="Numbered"/> put after it: "chat" of "chat 2".</summary>
    private static string StemOf(string handle) =>
        handle.LastIndexOf(' ') is > 0 and var space && handle[(space + 1)..].All(char.IsAsciiDigit) ? handle[..space] : handle;

    /// <summary>
    /// The handle a project had that is back under a new ID: the same server, root and session id (its ID's last
    /// part), as when its profile is renamed and its sessions are deleted and created again.
    /// </summary>
    private string? Returning(ProjectRef project, string? root) =>
        _retired.Where(r => r.Value.Project.ServerId == project.ServerId
                && string.Equals(r.Value.Entry.Root, root, StringComparison.OrdinalIgnoreCase)
                && SessionId(r.Value.Project.ProjectId) == SessionId(project.ProjectId))
            .Select(r => r.Key)
            .FirstOrDefault();

    private static string SessionId(string projectId) => projectId.Split('/')[^1];

    private bool Free(string handle) => !_byHandle.ContainsKey(handle) && !_retired.ContainsKey(handle);

    private static bool Same(string? name, string reference) =>
        name is not null && name.Equals(reference, StringComparison.OrdinalIgnoreCase);

    private static double Closeness(string a, string b) => FuzzyMatch.JaroWinkler(FuzzyMatch.Normalize(a), FuzzyMatch.Normalize(b));

    /// <summary>
    /// The issue number, then the name's distinctive words, then the kind, each at most <see cref="MaxLength"/> long.
    /// A word of the name that is the kind tells it apart from its siblings no better than the kind, so it comes last.
    /// </summary>
    private static IEnumerable<string> Candidates(string name, string? kind)
    {
        if (IssueNumber().Match(name) is { Success: true } number)
            yield return number.Value;
        foreach (var word in Words(name).Where(w => w.Length >= 3 && !Undistinctive.Contains(w) && !w.All(char.IsDigit) && !Same(kind, w)))
            yield return Shortened(word, MaxLength);
        if (kind is not null && Words(kind).FirstOrDefault(w => !w.All(char.IsDigit)) is { } kindWord)
            yield return Shortened(kindWord, MaxLength);
    }

    /// <summary>When every candidate is taken: the first one with the lowest free number after it ("chat 2").</summary>
    private string Numbered(string name, string? kind)
    {
        var stem = Shortened(Candidates(name, kind).FirstOrDefault() ?? "projekt", MaxLength - 3);
        for (var n = 2; ; n++)
        {
            var handle = $"{stem} {n}";
            if (Free(handle)) return handle;
        }
    }

    private static string Shortened(string word, int length) => (word.Length <= length ? word : word[..length]).ToLowerInvariant();

    private static IEnumerable<string> Words(string text) => Word().Matches(text).Select(m => m.Value);

    /// <summary>Without what a speaker puts around a reference: punctuation, and "projekt"/"project"/"sag"/"nummer".</summary>
    private static string Clean(string spoken)
    {
        var text = spoken.Trim().Trim('.', ',', '!', '?', '"', '\'', ' ');
        return Lead().Replace(text, "").Trim();
    }

    [GeneratedRegex(@"(?<!\d)\d{1,6}(?!\d)")]
    private static partial Regex IssueNumber();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"^(projekt(et)?|project|sag(en)?|nummer|number|nr\.?)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Lead();
}
