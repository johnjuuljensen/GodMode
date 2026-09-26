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
/// ("vonage"). A project keeps its handle for the session, and no two projects have one, across servers.
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
    private readonly Dictionary<ProjectRef, (string Handle, string Name)> _byProject = [];
    private readonly Dictionary<string, ProjectRef> _byHandle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every handle given so far, in the order they were given.</summary>
    public IReadOnlyList<string> All
    {
        get { lock (_lock) return [.. _byHandle.Keys]; }
    }

    /// <summary>The project's handle, given now if it has none yet.</summary>
    public string For(ProjectRef project, string name)
    {
        lock (_lock)
        {
            if (_byProject.TryGetValue(project, out var known))
                return known.Handle;

            var handle = Candidates(name).FirstOrDefault(c => !_byHandle.ContainsKey(c)) ?? Numbered(name);
            _byProject[project] = (handle, name);
            _byHandle[handle] = project;
            return handle;
        }
    }

    /// <summary>The handle given to the project, or null when it has none.</summary>
    public string? Of(ProjectRef project)
    {
        lock (_lock) return _byProject.TryGetValue(project, out var known) ? known.Handle : null;
    }

    /// <summary>
    /// The project a spoken reference names: a handle, a number said in digits or Danish words ("to hundrede og
    /// treogfirs"), a word of a project's name that only one project has, or a handle misheard slightly.
    /// Null when it names none, or more than one.
    /// </summary>
    public ProjectRef? Resolve(string spoken)
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

            var close = _byHandle
                .Select(h => (h.Value, Score: FuzzyMatch.JaroWinkler(FuzzyMatch.Normalize(h.Key), FuzzyMatch.Normalize(reference))))
                .Where(h => h.Score >= 0.9)
                .OrderByDescending(h => h.Score)
                .ToList();
            return close.Count == 1 || (close.Count > 1 && close[0].Score > close[1].Score) ? close[0].Value : null;
        }
    }

    /// <summary>The issue number, then the name's distinctive words, each at most <see cref="MaxLength"/> long.</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        if (IssueNumber().Match(name) is { Success: true } number)
            yield return number.Value;
        foreach (var word in Words(name).Where(w => w.Length >= 3 && !Undistinctive.Contains(w) && !w.All(char.IsDigit)))
            yield return word.Length <= MaxLength ? word.ToLowerInvariant() : word[..MaxLength].ToLowerInvariant();
    }

    /// <summary>When every candidate is taken: the first one with the lowest free number after it ("vonage 2").</summary>
    private string Numbered(string name)
    {
        var stem = Candidates(name).FirstOrDefault() ?? "projekt";
        stem = stem.Length <= MaxLength - 3 ? stem : stem[..(MaxLength - 3)];
        for (var n = 2; ; n++)
        {
            var handle = $"{stem} {n}";
            if (!_byHandle.ContainsKey(handle)) return handle;
        }
    }

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
