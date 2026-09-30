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
/// servers. A project that is gone is forgotten (<see cref="Forget"/>), and its handle is free again.
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

            var handle = Candidates(name, kind).FirstOrDefault(c => !_byHandle.ContainsKey(c)) ?? Numbered(name, kind);
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

    /// <summary>The project is gone: it names nothing any more, and its handle may be given again.</summary>
    public void Forget(ProjectRef project)
    {
        lock (_lock)
        {
            if (_byProject.Remove(project, out var known))
                _byHandle.Remove(known.Handle);
        }
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
            if (!_byHandle.ContainsKey(handle)) return handle;
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
