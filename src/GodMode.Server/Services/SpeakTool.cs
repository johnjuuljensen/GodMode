using System.ComponentModel;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GodMode.Server.Auth;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodMode.Server.Services;

/// <summary>
/// <c>speak</c>, on GodMode's own MCP endpoint beside <see cref="PermissionPromptTool"/> and <see cref="MessageParentTool"/>
/// (issue #384): a session gives the spoken version of its reply, which voice says word for word in place of a summary
/// of the reply it wrote for a screen. Every session is asked to, on every turn (<see cref="Instructions"/>). The call
/// only checks the text (<see cref="Check"/>), and refuses one voice could not say with why; the text it accepted is the
/// turn's from claude's own stream (<see cref="SpokenReply"/>), so it is bound to the turn it was made in, and to the
/// session's main conversation, not a subagent's.
/// </summary>
[McpServerToolType]
[Authorize(Policy = GodModeAuthExtensions.ProjectPolicy)]
public sealed partial class SpeakTool(ILogger<SpeakTool> logger)
{
    public const string Name = "speak";

    /// <summary>The most characters a spoken text may have, its whitespace collapsed: a sentence or two, said in some 15 seconds.</summary>
    public const int MaxLength = 300;

    /// <summary>
    /// What the MCP server tells every session on <see cref="McpEndpointUrl.Path"/> (its <c>instructions</c>, which
    /// Claude Code puts in the session's system prompt, so it holds on every turn): call <c>speak</c> each turn.
    /// </summary>
    public const string Instructions =
        "The user may follow this session by voice, hands-free, with no screen. On every turn, whether or not they are " +
        "listening now, call the " + Name + " tool once, as the last thing before your final reply, with a short spoken " +
        "version of that reply: one or two plain sentences, at most 300 characters, in the language you reply in, ending " +
        "with your question if you have one. Then write your full reply as usual. Call it from your main conversation " +
        "only, never from a subagent. If it refuses the text, it says why: fix the text and call it again. " +
        "When where the session stands changes (a pull request opened, the tests went green, you are blocked, the work " +
        "is done), also give its recap: one plain line of where the session stands, not what this turn did, e.g. \"Pull " +
        "request 456 is open, the tests pass, waiting for review\". It is kept until you give another, so not every turn.";

    /// <summary>The most characters a recap may have, its whitespace collapsed: one line (issue #466).</summary>
    public const int RecapMaxLength = 200;

    [McpServerTool(Name = Name)]
    [Description("Gives the spoken version of this turn's reply, which GodMode's voice says word for word to a user who " +
        "listens rather than reads. Call it once on every turn, as the last thing before your final reply, from your main " +
        "conversation (never a subagent): one or two plain spoken sentences, at most 300 characters, ending with your " +
        "question if you have one. No markdown, code, paths, URLs or lists: say what they are instead. The full reply is " +
        "still written as usual, and stays on screen. A later call in the same turn replaces an earlier one. With a " +
        "recap, it also keeps the session's one line of where it stands until another replaces it. Refused, with why, " +
        "for a text or recap voice cannot say; fix it and call again.")]
    public string Speak(
        RequestContext<CallToolRequestParams> context,
        [Description("What to say, e.g. \"The fix is pushed and the tests pass. Shall I open the pull request?\"")] string text,
        [Description("Only when where the session stands changed: one plain line of where it stands now, not what this " +
            "turn did, e.g. \"Pull request 456 is open, the tests pass, waiting for review\". At most 200 characters. " +
            "Kept until a later call gives another.")] string? recap = null)
    {
        var projectId = context.User?.FindFirstValue(GodModeAuthExtensions.ProjectIdClaim)
            ?? throw new McpException("The caller is not a project");
        var (spoken, refused) = Check(text);
        var (recapped, recapRefused) = CheckRecap(recap);
        if ((refused ?? recapRefused) is { } why)
        {
            logger.LogInformation("Project {ProjectId}: speak refused: {Reason}", projectId, why);
            throw new McpException(why);
        }
        return recapped is null
            ? $"Kept as this turn's spoken reply: \"{spoken}\". Now write your full reply as usual, unless you already have."
            : $"Kept as this turn's spoken reply: \"{spoken}\", and as the session's recap: \"{recapped}\". Now write your full reply as usual, unless you already have.";
    }

    /// <summary>
    /// The text as voice says it, its whitespace collapsed, or why it cannot be said: it is empty, longer than
    /// <see cref="MaxLength"/>, or written for a screen (markdown, code, a URL, a list).
    /// </summary>
    public static (string Spoken, string? Refused) Check(string? text) => Check(text, "text", MaxLength,
        "say less, in one or two sentences; the full reply on screen has the rest.");

    /// <summary>
    /// The recap as voice says it, or why it cannot be said, as <see cref="Check(string?)"/> checks a text, at most
    /// <see cref="RecapMaxLength"/> characters. A recap that is missing or blank is none: (null, null).
    /// </summary>
    public static (string? Recap, string? Refused) CheckRecap(string? recap) =>
        string.IsNullOrWhiteSpace(recap) ? (null, null)
            : Check(recap, "recap", RecapMaxLength, "say only where the session stands, in one line.");

    private static (string Spoken, string? Refused) Check(string? text, string what, int maxLength, string sayLess)
    {
        var spoken = Normalize(text);
        return spoken switch
        {
            "" => (spoken, $"The {what} is empty: give one or two spoken sentences."),
            _ when spoken.Length > maxLength => (spoken, $"The {what} is {spoken.Length} characters, and at most {maxLength} can be spoken: {sayLess}"),
            _ when Url().IsMatch(spoken) => (spoken, $"The {what} has a URL, which cannot be spoken: say what it is instead."),
            _ when ListItem().IsMatch(text!) => (spoken, $"The {what} is a list: say it as plain sentences."),
            _ when spoken.IndexOfAny(ScreenCharacters) is var at and >= 0 => (spoken,
                $"The {what} has '{spoken[at]}', which is markdown or code and cannot be spoken: write plain sentences, " +
                "with no markdown, code or paths (say \"pull request 413\", not \"#413\")."),
            _ => (spoken, null),
        };
    }

    /// <summary>The text trimmed, with each run of whitespace (line breaks included) one space; empty for none.</summary>
    public static string Normalize(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    /// <summary>Characters spoken text has no use for, and markdown or code does: emphasis, headings, tables, links, code, paths.</summary>
    private static readonly char[] ScreenCharacters = ['`', '*', '_', '#', '|', '[', ']', '<', '>', '{', '}', '\\', '~'];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <summary>
    /// A line that starts as a list item does: "- ", "+ ", or, on a line after the first, "1. ". A text that starts with a
    /// number and a dot is a sentence ("1. maj er releasen klar", "2. gang virkede det"), not a list: a numbered list has
    /// its items on lines of their own.
    /// </summary>
    [GeneratedRegex(@"(^|\n)[ \t]*[-+][ \t]|\n[ \t]*\d+[.)][ \t]")]
    private static partial Regex ListItem();
}
