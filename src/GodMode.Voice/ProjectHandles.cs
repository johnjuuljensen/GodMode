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

    /// <summary>
    /// What a handle was given for: the project's name, and its root, kind and profile once known, and its root's
    /// title (#434), when it is shown as other than its name. The root is accepted as either.
    /// </summary>
    private sealed record Entry(string Handle, string Name, string? Root, string? Kind, string? Profile = null, string? RootTitle = null)
    {
        public bool InRoot(string? reference) => Same(Root, reference ?? "") || Same(RootTitle, reference ?? "");

        public string Label => ProjectHandles.Label(Handle, Name, Kind);
    }

    /// <summary>Every handle given so far, in the order they were given.</summary>
    public IReadOnlyList<string> All
    {
        get { lock (_lock) return [.. _byHandle.Keys]; }
    }

    /// <summary>
    /// The project's handle, given now if it has none yet. Its <paramref name="root"/> and <paramref name="kind"/>
    /// (<see cref="GodMode.Shared.Models.ProjectSummary.Kind"/>) name it too, when no other project has them, and
    /// its root or <paramref name="profile"/> said with its handle or label names it among several (#450); given
    /// later, they are kept, but the handle stays.
    /// </summary>
    public string For(ProjectRef project, string name, string? root = null, string? kind = null, string? profile = null)
    {
        lock (_lock)
        {
            if (_byProject.TryGetValue(project, out var known))
            {
                if ((known.Root is null && root is not null) || (known.Kind is null && kind is not null) || (known.Profile is null && profile is not null))
                    _byProject[project] = known with { Root = known.Root ?? root, Kind = known.Kind ?? kind, Profile = known.Profile ?? profile };
                return known.Handle;
            }

            var handle = Returning(project, root)
                ?? Candidates(name, kind).FirstOrDefault(Free) ?? Numbered(name, kind);
            _retired.Remove(handle);
            _byProject[project] = new Entry(handle, name, root, kind, profile);
            _byHandle[handle] = project;
            return handle;
        }
    }

    /// <summary>
    /// What the project's root is shown as now (#434): its title, or its name. Said with its handle, and accepted
    /// besides the root's name.
    /// </summary>
    public void Retitle(ProjectRef project, string? shown)
    {
        lock (_lock)
        {
            if (_byProject.TryGetValue(project, out var known))
                _byProject[project] = known with { RootTitle = shown is null || Same(known.Root, shown) ? null : shown };
        }
    }

    /// <summary>The handle given to the project, or null when it has none.</summary>
    public string? Of(ProjectRef project)
    {
        lock (_lock) return _byProject.TryGetValue(project, out var known) ? known.Handle : null;
    }

    /// <summary>The project's handle as it is said (<see cref="Label(string, string, string?)"/>), or null when it has none.</summary>
    public string? LabelOf(ProjectRef project)
    {
        lock (_lock) return _byProject.TryGetValue(project, out var known) ? known.Label : null;
    }

    /// <summary>
    /// A handle as it is said, so it says what it is (#450): an issue number as "issue 376", a word with the project's
    /// kind before it ("branch master", "chat testing"), unless it is the kind already ("chat", "chat 2"). A handle that
    /// is the kind alone, given when the name's word was taken ("branch" for a second "master"), says the name's word:
    /// "branch master", as one numbered for it does ("master 2" of a third). Not unique, as a handle is: the root and profile it is said with tell two apart
    /// (<see cref="ProjectNames"/>), and <see cref="Resolve"/> takes them.
    /// </summary>
    public static string Label(string handle, string name, string? kind)
    {
        if (handle.All(char.IsAsciiDigit))
            return $"issue {handle}";
        if (kind is null || Words(kind).FirstOrDefault(w => !w.All(char.IsDigit))?.ToLowerInvariant() is not { } kindWord)
            return handle;
        if (Same(StemOf(handle), kindWord) || handle.StartsWith(kindWord + " ", StringComparison.OrdinalIgnoreCase))
            return Same(handle, kindWord) && Candidates(name, kind).FirstOrDefault(c => !c.All(char.IsAsciiDigit) && !Same(c, kindWord)) is { } word
                ? $"{kindWord} {word}"
                : handle;
        // A number put after a word that was taken ("master 2") says nothing: the root and profile tell them apart
        return $"{kindWord} {StemOf(handle)}";
    }

    /// <summary>The projects whose label (<see cref="Label(string, string, string?)"/>) the reference is, said alone.</summary>
    public IReadOnlyList<ProjectRef> Labelled(string spoken)
    {
        var reference = Clean(spoken);
        lock (_lock) return [.. _byProject.Where(p => Same(p.Value.Label, reference)).Select(p => p.Key)];
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
    /// ("Assistent"). It may be said as it is spoken (#450): its label ("issue 283", "branch master"), with the root or
    /// profile that tells it from another ("branch master i GodMode, profil Mega"). A number is never matched fuzzily:
    /// "28" is not "283". Null when it names none, or more than one.
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
            // "issue 283", the number in digits or words, is the handle 283
            if (IssueLead().Match(reference) is { Success: true } issue && DanishNumbers.Parse(issue.Groups[1].Value) is { } issued)
                return _byHandle.GetValueOrDefault(issued.ToString());
            // Said as it is spoken: its label, alone or with its root or profile; a label several have names none
            var labelled = _byProject.Where(p => Same(p.Value.Label, reference)).Select(p => p.Key).ToList();
            if (labelled.Count > 0)
                return labelled is [var single] ? single : null;
            if (Qualified(reference) is { } qualified)
                return qualified is [var one] ? one : null;
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
            var grouped = _byProject.Where(p => p.Value.InRoot(reference) || Same(p.Value.Kind, reference)).Select(p => p.Key).ToList();
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
            var roots = _byProject.Where(p => new[] { p.Value.Root, p.Value.RootTitle }.OfType<string>().Any(root => Closeness(root, reference) >= 0.9))
                .Select(p => p.Key).ToList();
            return roots is [var only] ? only : null;
        }
    }

    /// <summary>
    /// The projects a reference names with a root or profile said in it ("master i GodMode, profil Mega", "Mega
    /// GodMode branch master"): those in every root or profile it says, whose handle, label, either without its number,
    /// or a word of whose name is the rest. A place said after "profil"/"profile" is a profile only: a profile and a
    /// root may have one name ("GodMode, profil Godmode"). Null when it says no root or profile, or nothing besides them.
    /// </summary>
    private List<ProjectRef>? Qualified(string reference)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        List<Func<Entry, bool>> said = [];
        var rest = reference;
        foreach (var profile in _byProject.Values.Select(e => e.Profile).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
        {
            var pattern = $@"\b(?:profil(?:en)?|profile)\s+{Regex.Escape(profile)}(?![\p{{L}}\p{{N}}])";
            if (!Regex.IsMatch(rest, pattern, Options)) continue;
            said.Add(e => Same(e.Profile, profile));
            rest = Regex.Replace(rest, pattern, " ", Options);
        }
        var places = _byProject.Values.SelectMany(e => new[] { e.Root, e.RootTitle, e.Profile }).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
        foreach (var place in places)
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(place)}(?![\p{{L}}\p{{N}}])";
            if (!Regex.IsMatch(rest, pattern, Options)) continue;
            said.Add(e => e.InRoot(place) || Same(e.Profile, place));
            rest = Regex.Replace(rest, pattern, " ", Options);
        }
        if (said.Count == 0) return null;
        rest = string.Join(' ', Words(rest).Where(w => !Connectives.Contains(w)));
        if (rest.Length == 0) return null;

        var number = DanishNumbers.Parse(rest)?.ToString();
        return [.. _byProject
            .Where(p => said.All(place => place(p.Value)))
            .Where(p => Same(p.Value.Handle, rest) || Same(p.Value.Label, rest) || Same(StemOf(p.Value.Handle), rest)
                || (!p.Value.Handle.All(char.IsAsciiDigit) && Same(StemOf(p.Value.Label), rest)) || (number is not null && Same(p.Value.Handle, number))
                || Words(p.Value.Name).Any(w => Same(w, rest)))
            .Select(p => p.Key)];
    }

    /// <summary>The words said between a root or profile and a project's name: "i GodMode, profil Mega".</summary>
    private static readonly HashSet<string> Connectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "in", "på", "hos", "under", "fra", "from", "of", "at", "profil", "profilen", "profile", "root", "roden", "rod",
    };

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

    [GeneratedRegex(@"^issue\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IssueLead();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"^(projekt(et)?|project|sag(en)?|nummer|number|nr\.?)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Lead();
}
