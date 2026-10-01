namespace GodMode.Server.Services;

/// <summary>
/// Where a parent link may cross a root, or a profile: <c>Fleet:Links:&lt;name&gt;</c> in the server's instance config,
/// each with <c>From</c> and <c>To</c>, each <c>&lt;profile&gt;/&lt;root&gt;</c> or <c>&lt;profile&gt;/*</c> (every root of the
/// profile). Kept by hand on the host, as roots and profiles are; never in a root's <c>.godmode-root</c>, which a session
/// working in its root can write. Read on every check, so an edit (or a removal) holds from the next call.
/// <para>
/// Without a link, a session's parent is in its own root: an overseer's <c>start_session</c> makes children in its root
/// alone, and a child's <c>message_parent</c> and the server's notices reach a parent in its root alone. A link lets the
/// sessions of <c>From</c> start children in <c>To</c> and see the sessions there with the fleet's tools, and lets those
/// children message back and their parent hear of them. Nothing else crosses: a session's fleet tools still see its own
/// profile, and the server's own credential is not scoped by links.
/// </para>
/// </summary>
public sealed class FleetLinks(IConfiguration configuration, ILogger logger)
{
    public const string Section = "Fleet:Links";

    /// <summary>The malformed entries logged so far, each once while it lasts unchanged.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Link, byte> _logged = new();

    /// <param name="Name">The entry's key.</param>
    /// <param name="From">The parent's end: <c>&lt;profile&gt;/&lt;root&gt;</c> or <c>&lt;profile&gt;/*</c>.</param>
    /// <param name="To">The children's end, the same way.</param>
    public sealed record Link(string Name, string From, string To);

    /// <summary>The configured links; one without a well-formed <c>From</c> and <c>To</c> is left out, and logged.</summary>
    public IReadOnlyList<Link> All() =>
        configuration.GetSection(Section).GetChildren()
            .Select(entry => new Link(entry.Key, entry["From"] ?? "", entry["To"] ?? ""))
            .Where(link =>
            {
                if (IsEnd(link.From) && IsEnd(link.To)) return true;
                if (_logged.TryAdd(link, 0)) logger.LogWarning("{Section}:{Name} is left out: From and To must each be <profile>/<root> or <profile>/* (From '{From}', To '{To}')",
                    Section, link.Name, link.From, link.To);
                return false;
            })
            .ToList();

    /// <summary>Whether a session in <paramref name="fromRoot"/> may be the parent of one in <paramref name="toRoot"/>: the same root, or a link.</summary>
    public bool AllowsParent(RootRef fromRoot, RootRef toRoot) => fromRoot == toRoot || Linked(fromRoot, toRoot);

    /// <summary>Whether a link lets a session in <paramref name="fromRoot"/> start children in, and see, <paramref name="toRoot"/>.</summary>
    public bool Linked(RootRef fromRoot, RootRef toRoot) =>
        All().Any(link => Matches(link.From, fromRoot) && Matches(link.To, toRoot));

    /// <summary>What a refusal says is missing: the link an entry would need.</summary>
    public static string Missing(RootRef fromRoot, RootRef toRoot) =>
        $"no {Section} entry links {fromRoot} to {toRoot} (From \"{fromRoot}\" or \"{fromRoot.Profile}/*\", To \"{toRoot}\" or \"{toRoot.Profile}/*\", in the server's instance config)";

    private static bool Matches(string end, RootRef root) =>
        end.Split('/') is [var profile, var name]
        && string.Equals(profile, root.Profile, StringComparison.Ordinal)
        && (name == "*" || string.Equals(name, root.Root, StringComparison.Ordinal));

    private static bool IsEnd(string end) => end.Split('/') is [{ Length: > 0 }, { Length: > 0 }];
}

/// <summary>A root, by its profile and its name.</summary>
public readonly record struct RootRef(string Profile, string Root)
{
    public override string ToString() => $"{Profile}/{Root}";

    /// <summary>The root of a session's opaque ID, <c>{profile}/{root}/{id}</c>; null for an ID of another shape.</summary>
    public static RootRef? OfId(string projectId) => projectId.Split('/') is [var profile, var root, _] ? new RootRef(profile, root) : null;
}
