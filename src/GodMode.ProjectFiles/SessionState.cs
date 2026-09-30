using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GodMode.ProjectFiles;

/// <summary>
/// Where a session keeps its state, and its id. Every session's state is in
/// <c>{working folder}/.godmode/sessions/{id}/</c>; the folder's <c>.godmode/.gitignore</c> keeps it all
/// out of git. The id is GodMode's own, <c>yymmdd-{kind}-{slug}-{suffix}</c> (<c>260929-feat-left-list-k7q2</c>),
/// unique within its root: not claude's session GUID, which is kept in the state as <c>session-id</c>.
/// </summary>
public static partial class SessionState
{
    /// <summary>The folder in <c>.godmode/</c> that holds one folder per session.</summary>
    public const string SessionsFolderName = "sessions";

    /// <summary>
    /// The folder in <c>.godmode/</c> where a deleted session's state waits, <c>trash/{id}/</c>, for an
    /// undo (<see cref="Restore"/>) until it is purged. Nothing in it is a session.
    /// </summary>
    public const string TrashFolderName = "trash";

    /// <summary>The file in a trashed state folder that says when it was trashed (round-trip UTC), for the purge.</summary>
    public const string TrashedAtFileName = "trashed-at";

    /// <summary>
    /// The file in a trashed state folder that says the session was forgotten, not deleted: its folder was
    /// left as it was, and a restore brings it back as it was, sharing its folder or not as its settings say.
    /// </summary>
    public const string ForgottenFileName = "forgotten";

    /// <summary>The state file a session's folder has: without it, a folder in <c>sessions/</c> is no session.</summary>
    public const string StatusFileName = "status.json";

    /// <summary>What the user sent the session, and what claude wrote: GodMode's own logs, in its state folder.</summary>
    public const string InputFileName = "input.jsonl";
    public const string OutputFileName = "output.jsonl";

    /// <summary>How long a slug of the name may be: the id is in paths, and Windows paths are short.</summary>
    public const int MaxSlugLength = 24;

    /// <summary>How long a kind may be in the id and its label.</summary>
    public const int MaxKindLength = 12;

    /// <summary>The kind when neither the create script nor the action name gives one that has a letter or digit.</summary>
    public const string DefaultKind = "session";

    /// <summary>How many random characters end the id.</summary>
    public const int SuffixLength = 4;

    /// <summary>The suffix's characters: base32 (RFC 4648), lowercase.</summary>
    private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary><c>yymmdd</c>, a kind, maybe a slug, and the suffix, in lowercase <c>[a-z0-9-]</c>.</summary>
    [GeneratedRegex("^[0-9]{6}-[a-z0-9]+(-[a-z0-9]+)*-[a-z2-7]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotSlug();

    /// <summary><c>{workingFolder}/.godmode/sessions/</c>.</summary>
    public static string SessionsPathOf(string workingFolder) =>
        Path.Combine(workingFolder, ProjectFolder.GodModeDirectoryName, SessionsFolderName);

    /// <summary>The state folder of session <paramref name="id"/> in <paramref name="workingFolder"/>: <c>.godmode/sessions/{id}/</c>.</summary>
    public static string PathOf(string workingFolder, string id) => Path.Combine(SessionsPathOf(workingFolder), id);

    /// <summary><c>{workingFolder}/.godmode/trash/</c>.</summary>
    public static string TrashPathOf(string workingFolder) =>
        Path.Combine(workingFolder, ProjectFolder.GodModeDirectoryName, TrashFolderName);

    /// <summary>Where session <paramref name="id"/>'s state is while it is in the trash: <c>.godmode/trash/{id}/</c>.</summary>
    public static string TrashedPathOf(string workingFolder, string id) => Path.Combine(TrashPathOf(workingFolder), id);

    /// <summary>
    /// Whether <paramref name="id"/> is a session id as <see cref="NewId"/> makes them. A folder in
    /// <c>sessions/</c> with any other name is no session: the session can write its working folder,
    /// and the id goes into paths and into the session's opaque ID.
    /// </summary>
    public static bool IsId(string? id) => id is { Length: > 0 and <= 64 } && IdPattern().IsMatch(id);

    /// <summary>
    /// The ids of the sessions in <paramref name="workingFolder"/>: each folder in
    /// <c>.godmode/sessions/</c> whose name <see cref="IsId"/> and that has a <c>status.json</c>, in
    /// ordinal order. A flat <c>.godmode/status.json</c>, the old layout, is no session.
    /// </summary>
    public static IReadOnlyList<string> List(string workingFolder)
    {
        var sessions = SessionsPathOf(workingFolder);
        if (!Directory.Exists(sessions)) return [];
        return Directory.GetDirectories(sessions)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(id => IsId(id) && File.Exists(Path.Combine(sessions, id, StatusFileName)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The ids in <paramref name="workingFolder"/>'s trash: each folder in <c>.godmode/trash/</c> whose
    /// name <see cref="IsId"/>, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> ListTrashed(string workingFolder)
    {
        var trash = TrashPathOf(workingFolder);
        if (!Directory.Exists(trash)) return [];
        return Directory.GetDirectories(trash)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsId)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Moves session <paramref name="id"/>'s state from <c>sessions/</c> to <c>trash/</c>, and marks
    /// when (<see cref="TrashedAtFileName"/>). One of that id in the trash already is replaced. False
    /// when the session has no state folder: nothing is moved. A session <paramref name="forgotten"/> is
    /// marked so (<see cref="ForgottenFileName"/>).
    /// </summary>
    public static bool Trash(string workingFolder, string id, DateTime at, bool forgotten = false)
    {
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a session id.", nameof(id));
        var from = PathOf(workingFolder, id);
        if (!Directory.Exists(from)) return false;
        var to = TrashedPathOf(workingFolder, id);
        Directory.CreateDirectory(TrashPathOf(workingFolder));
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.Move(from, to);
        File.WriteAllText(Path.Combine(to, TrashedAtFileName), at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (forgotten) File.WriteAllText(Path.Combine(to, ForgottenFileName), string.Empty);
        return true;
    }

    /// <summary>Whether session <paramref name="id"/> in the trash of <paramref name="workingFolder"/> was forgotten rather than deleted.</summary>
    public static bool WasForgotten(string workingFolder, string id) =>
        File.Exists(Path.Combine(TrashedPathOf(workingFolder, id), ForgottenFileName));

    /// <summary>
    /// Moves session <paramref name="id"/>'s state back from <c>trash/</c> to <c>sessions/</c>, without
    /// its <see cref="TrashedAtFileName"/>. Throws when it is not in the trash, or <c>sessions/</c> has
    /// one of that id; nothing is moved then.
    /// </summary>
    public static string Restore(string workingFolder, string id)
    {
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a session id.", nameof(id));
        var from = TrashedPathOf(workingFolder, id);
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"Session {id} is not in the trash of {workingFolder}.");
        var to = PathOf(workingFolder, id);
        if (Directory.Exists(to)) throw new IOException($"Session {id} has a state folder in {workingFolder} already.");
        Directory.CreateDirectory(SessionsPathOf(workingFolder));
        Directory.Move(from, to);
        File.Delete(Path.Combine(to, TrashedAtFileName));
        File.Delete(Path.Combine(to, ForgottenFileName));
        return to;
    }

    /// <summary>
    /// When session <paramref name="id"/> was trashed, by its <see cref="TrashedAtFileName"/>; when that
    /// is missing or cannot be read, the folder's last write, so a trash without it is purged too.
    /// </summary>
    public static DateTime TrashedAt(string workingFolder, string id)
    {
        var folder = TrashedPathOf(workingFolder, id);
        try
        {
            if (DateTime.TryParse(File.ReadAllText(Path.Combine(folder, TrashedAtFileName)).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at))
                return at.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Directory.GetLastWriteTimeUtc(folder);
    }

    /// <summary>
    /// An id: <c>yymmdd</c> of <paramref name="createdAt"/>, the <see cref="Kind"/>, the
    /// <see cref="Slug"/> of <paramref name="name"/> (left out when it has none), and
    /// <paramref name="suffix"/> (<see cref="NewSuffix"/>). Unique only by chance: the caller checks its root.
    /// </summary>
    public static string Id(DateTime createdAt, string kind, string name, string suffix) =>
        Join(createdAt.ToString("yyMMdd", CultureInfo.InvariantCulture), Kind(kind), Slug(name), suffix);

    /// <summary><see cref="SuffixLength"/> random base32 characters, lowercase.</summary>
    public static string NewSuffix() => new(RandomNumberGenerator.GetItems<char>(SuffixAlphabet, SuffixLength));

    /// <summary>
    /// A kind as the id and the label have it: its <see cref="Slug"/>, cut at <see cref="MaxKindLength"/>,
    /// or <see cref="DefaultKind"/> when that leaves nothing.
    /// </summary>
    public static string Kind(string? kind) => Slug(kind, MaxKindLength) is { Length: > 0 } slug ? slug : DefaultKind;

    /// <summary>
    /// <paramref name="text"/> in lowercase <c>[a-z0-9-]</c>: accents dropped (<c>é</c> is <c>e</c>),
    /// every run of anything else one <c>-</c>, none at either end, cut at <paramref name="maxLength"/>.
    /// Empty when nothing is left.
    /// </summary>
    public static string Slug(string? text, int maxLength = MaxSlugLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var plain = new StringBuilder(text.Length);
        foreach (var c in Spelled(text).Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(c));
        var slug = NotSlug().Replace(plain.ToString(), "-").Trim('-');
        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }

    /// <summary>The letters that are no letter with an accent spelled as Danish and German spell them without: <c>æ</c> is <c>ae</c>, <c>ø</c> <c>oe</c>, <c>å</c> <c>aa</c>.</summary>
    private static string Spelled(string text) => text
        .Replace("æ", "ae").Replace("Æ", "Ae").Replace("ø", "oe").Replace("Ø", "Oe")
        .Replace("å", "aa").Replace("Å", "Aa").Replace("ß", "ss");

    private static string Join(params string[] parts) => string.Join('-', parts.Where(part => part.Length > 0));

    /// <summary>
    /// Makes the session's state folder, and the working folder's <c>.godmode/.gitignore</c>
    /// (<see cref="ProjectFolder.EnsureGitIgnore"/>) first, so nothing in it reaches git. <c>.godmode</c>
    /// is hidden on Windows.
    /// </summary>
    public static string Create(string workingFolder, string id)
    {
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a session id.", nameof(id));
        ProjectFolder.EnsureGitIgnore(workingFolder);
        if (OperatingSystem.IsWindows())
        {
            var godMode = Path.Combine(workingFolder, ProjectFolder.GodModeDirectoryName);
            File.SetAttributes(godMode, File.GetAttributes(godMode) | FileAttributes.Hidden);
        }
        var path = PathOf(workingFolder, id);
        Directory.CreateDirectory(path);
        // Its logs from the start, empty: a reader before claude's first line finds no output, not no file
        foreach (var log in new[] { InputFileName, OutputFileName })
            if (!File.Exists(Path.Combine(path, log)))
                File.WriteAllText(Path.Combine(path, log), string.Empty);
        return path;
    }
}
