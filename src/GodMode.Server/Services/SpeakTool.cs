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
        "only, never from a subagent. If it refuses the text, it says why: fix the text and call it again.";

    [McpServerTool(Name = Name)]
    [Description("Gives the spoken version of this turn's reply, which GodMode's voice says word for word to a user who " +
        "listens rather than reads. Call it once on every turn, as the last thing before your final reply, from your main " +
        "conversation (never a subagent): one or two plain spoken sentences, at most 300 characters, ending with your " +
        "question if you have one. No markdown, code, paths, URLs or lists: say what they are instead. The full reply is " +
        "still written as usual, and stays on screen. A later call in the same turn replaces an earlier one. Refused, " +
        "with why, for a text voice cannot say; fix it and call again.")]
    public string Speak(
        RequestContext<CallToolRequestParams> context,
        [Description("What to say, e.g. \"The fix is pushed and the tests pass. Shall I open the pull request?\"")] string text)
    {
        var projectId = context.User?.FindFirstValue(GodModeAuthExtensions.ProjectIdClaim)
            ?? throw new McpException("The caller is not a project");
        var (spoken, refused) = Check(text);
        if (refused is not null)
        {
            logger.LogInformation("Project {ProjectId}: speak refused: {Reason}", projectId, refused);
            throw new McpException(refused);
        }
        return $"Kept as this turn's spoken reply: \"{spoken}\". Now write your full reply as usual.";
    }

    /// <summary>
    /// The text as voice says it, its whitespace collapsed, or why it cannot be said: it is empty, longer than
    /// <see cref="MaxLength"/>, or written for a screen (markdown, code, a URL, a list).
    /// </summary>
    public static (string Spoken, string? Refused) Check(string? text)
    {
        var spoken = Normalize(text);
        return spoken switch
        {
            "" => (spoken, "The text is empty: give one or two spoken sentences."),
            { Length: > MaxLength } => (spoken, $"The text is {spoken.Length} characters, and at most {MaxLength} can be spoken: " +
                "say less, in one or two sentences; the full reply on screen has the rest."),
            _ when Url().IsMatch(spoken) => (spoken, "The text has a URL, which cannot be spoken: say what it is instead."),
            _ when ListItem().IsMatch(text!) => (spoken, "The text is a list: say it as one or two plain sentences."),
            _ when spoken.IndexOfAny(ScreenCharacters) is var at and >= 0 => (spoken,
                $"The text has '{spoken[at]}', which is markdown or code and cannot be spoken: write plain sentences, " +
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

    /// <summary>A line that starts as a list item does: "- ", "+ ", "1. ".</summary>
    [GeneratedRegex(@"(^|\n)[ \t]*([-+]|\d+[.)])[ \t]")]
    private static partial Regex ListItem();
}
