using System.Text.RegularExpressions;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// The slash commands GodMode passes to claude (#31). claude runs a leading <c>/name</c> in a message as a
/// command, and takes one it does not know (<c>/frobnicate</c>, <c>/tmp/x is full</c>) as text. GodMode
/// passes <see cref="Supported"/> and the session's skills, refuses claude's other commands, and passes
/// any other text on. Every input that reaches claude as a message is checked: the app's, voice's, the
/// fleet's <c>send</c> and a create's prompt.
/// <list type="bullet">
/// <item><c>/clear</c>: claude starts a new conversation (<c>conversation_reset</c>), and the session's output starts over (<see cref="OutputLog.RestartAsync"/>).</item>
/// <item><c>/compact</c>: claude summarises the conversation (<c>compact_boundary</c>), which the app shows as a marker.</item>
/// <item><c>/context</c>: claude's context usage, as a turn's reply.</item>
/// <item>Skills: prompts, all of them, as the session's last <c>system/init</c> named them.</item>
/// </list>
/// </summary>
public static partial class SlashCommands
{
    /// <summary>claude's commands that GodMode passes, besides the session's skills.</summary>
    public static readonly IReadOnlyList<string> Supported = ["clear", "compact", "context"];

    /// <summary>
    /// claude's commands that GodMode refuses even before a session's first <c>system/init</c> has listed its own
    /// (<see cref="ProjectStatus.ClaudeCommands"/>): what claude 2.1 lists, and what it has had. A skill of the
    /// same name is passed all the same.
    /// </summary>
    private static readonly HashSet<string> KnownBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "add-dir", "advisor", "agents", "auto-mode-setup", "autocompact", "bashes", "btw", "bug", "clear", "color",
        "compact", "config", "context", "copy", "cost", "design-consent", "design-revoke", "doctor", "effort", "exit",
        "export", "extra-usage", "fast", "feedback", "focus", "goal", "heapdump", "help", "hooks", "ide", "import",
        "init", "insights", "install-github-app", "keybindings", "list-agents", "login", "logout", "mcp", "memory",
        "migrate-installer", "model", "output-style", "permissions", "plan", "plugin", "pr-comments",
        "privacy-settings", "quit", "recap", "release-notes", "reload-plugins", "reload-skills", "rename", "resume",
        "review", "rewind", "sandbox", "security-review", "skill-doctor", "stats", "status", "statusline", "tasks",
        "team-onboarding", "terminal-setup", "theme", "todos", "upgrade", "usage", "usage-credits", "vim",
        "workflow-launch-exec", "__remote-workflow",
    };

    /// <summary>A leading <c>/name</c>, then white space or the end: a path (<c>/tmp/x</c>) is no command.</summary>
    [GeneratedRegex(@"^\s*/(?<name>[A-Za-z0-9_][A-Za-z0-9_:.\-]*)(?=\s|$)")]
    private static partial Regex CommandPattern();

    /// <summary>The command <paramref name="input"/> starts with, without its <c>/</c>; null when it starts with none.</summary>
    public static string? CommandOf(string? input) =>
        input != null && CommandPattern().Match(input) is { Success: true } match ? match.Groups["name"].Value : null;

    /// <summary>
    /// The commands GodMode passes to a session whose claude listed <paramref name="skills"/>: <see cref="Supported"/>
    /// and those, in that order, each once. What the app completes in its composer.
    /// </summary>
    public static IReadOnlyList<string> Passed(IEnumerable<string> skills) =>
        Supported.Concat(skills).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Why <paramref name="input"/> is not passed to the session, or null when it is: it starts with none of claude's
    /// commands, or with one GodMode passes (<see cref="Supported"/>, or one of the session's skills). With no
    /// <paramref name="status"/> (a create's prompt), or one whose claude has not started yet, the skills are not known:
    /// a skill is taken for text, and claude runs it all the same.
    /// </summary>
    public static string? WhyRefused(string? input, ProjectStatus? status)
    {
        if (CommandOf(input) is not { } name) return null;
        var passed = status?.SlashCommands ?? Supported;
        if (passed.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

        var allowed = $"GodMode passes {string.Join(", ", Supported.Select(c => $"/{c}"))} and the session's skills.";
        return name.ToLowerInvariant() switch
        {
            "model" or "effort" =>
                $"/{name} is not sent: GodMode sets a session's model and effort (its root's or action's, kept with the project) at each launch, which would undo it. {allowed}",
            "rename" =>
                $"/rename is not sent: GodMode names the session's claude, and sessions message each other by that name. {allowed}",
            _ when KnownBuiltIns.Contains(name) || status?.ClaudeCommands?.Contains(name, StringComparer.OrdinalIgnoreCase) == true =>
                $"/{name} is not one of the commands GodMode sends to claude. {allowed}",
            // claude does not know it either: it takes it as text
            _ => null,
        };
    }

    /// <summary>Refuses <paramref name="input"/> (<see cref="InvalidOperationException"/>) when <see cref="WhyRefused"/> says why.</summary>
    public static void Check(string? input, ProjectStatus? status)
    {
        if (WhyRefused(input, status) is { } why) throw new InvalidOperationException(why);
    }
}
