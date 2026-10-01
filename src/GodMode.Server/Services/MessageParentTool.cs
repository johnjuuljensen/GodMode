using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using GodMode.Server.Auth;
using GodMode.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodMode.Server.Services;

/// <summary>
/// <c>message_parent</c>, on GodMode's own MCP endpoint beside <see cref="PermissionPromptTool"/>: a session messages the
/// session that started it (its parent), through the server. The way a child reaches a parent that Claude Code's own
/// channel does not: one under another <c>CLAUDE_CONFIG_DIR</c>, or one that is stopped (the message waits on disk for
/// its resume). The parent gets it labelled with the sender, as one input once it can take one: never as the answer to
/// its permission prompt or question, never in the middle of its turn (<see cref="IProjectManager.MessageParentAsync"/>).
/// The sender is the session the caller's project token was issued to.
/// </summary>
[McpServerToolType]
[Authorize(Policy = GodModeAuthExtensions.ProjectPolicy)]
public sealed class MessageParentTool(IProjectManager projects, ILogger<MessageParentTool> logger)
{
    public const string Name = "message_parent";

    [McpServerTool(Name = Name)]
    [Description("Sends a message to your parent: the GodMode session that started you (your overseer). It reaches it labelled " +
        "as yours, as its next input once it can take one: at once when it is idle, after its turn when it is working, after " +
        "the user has answered when it waits on the user, and with its resume when it is stopped. Returns whether it was " +
        "delivered now or held, and why. Refused when you have no parent, and while 50 messages or 64000 characters are held for it. At most 8000 characters.")]
    public async Task<string> MessageParentAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("The message: a report, a question you parked, or feedback")] string text)
    {
        var projectId = context.User?.FindFirstValue(GodModeAuthExtensions.ProjectIdClaim)
            ?? throw new McpException("The caller is not a project");
        try
        {
            return JsonSerializer.Serialize(await projects.MessageParentAsync(projectId, text), JsonDefaults.Compact);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException)
        {
            logger.LogInformation("Project {ProjectId}: message_parent refused: {Reason}", projectId, ex.Message);
            throw new McpException(ex.Message, ex);
        }
    }
}
