using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.Server.Auth;
using GodMode.Server.Models;
using GodMode.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodMode.Server.Services;

/// <summary>
/// The one tool GodMode's MCP endpoint serves: claude's <c>--permission-prompt-tool</c>, which it
/// also puts its AskUserQuestion calls to. It waits for the user's answer however long that takes
/// (<see cref="IProjectManager.RequestPermissionAsync"/>) and returns claude the answer as JSON text.
/// The project is the one the caller's project token was issued to. While it waits, it reports
/// progress every <see cref="KeepAliveSetting"/> seconds: claude gives up on a tool call that sends
/// no response or progress for 300 seconds.
/// </summary>
[McpServerToolType]
public sealed class PermissionPromptTool(IProjectManager projects, IConfiguration configuration)
{
    public const string Name = "permission_prompt";

    /// <summary>How often a waiting call reports progress, in seconds.</summary>
    public const string KeepAliveSetting = "PermissionPromptKeepAliveSeconds";

    /// <summary>What claude waits for a tool call that reports nothing before it gives up on it.</summary>
    private const double ClaudeToolCallTimeoutSeconds = 300;

    private readonly TimeSpan _keepAlive = KeepAliveFrom(configuration);

    /// <summary>
    /// The configured keep-alive (default 30 seconds). It must be more than 0, or the wait would spin,
    /// and less than claude's 300 seconds, or claude would give up first. Anything else throws
    /// <see cref="StartupConfigurationException"/>: the server checks it at startup.
    /// </summary>
    public static TimeSpan KeepAliveFrom(IConfiguration configuration)
    {
        double seconds;
        try { seconds = configuration.GetValue(KeepAliveSetting, 30.0); }
        catch (InvalidOperationException) { seconds = double.NaN; } // not a number
        return seconds is > 0 and < ClaudeToolCallTimeoutSeconds
            ? TimeSpan.FromSeconds(seconds)
            : throw new StartupConfigurationException(
                $"GodMode.Server will not start: {KeepAliveSetting} is '{configuration[KeepAliveSetting]}'. It must be more than 0 " +
                $"and less than {ClaudeToolCallTimeoutSeconds} seconds: claude gives up on a tool call that reports nothing for {ClaudeToolCallTimeoutSeconds}.");
    }

    [McpServerTool(Name = Name)]
    [Description("GodMode's permission prompt: asks the user whether a tool call may run, and answers AskUserQuestion. " +
        "Called by Claude Code itself (--permission-prompt-tool), not meant to be called directly.")]
    public async Task<string> AskAsync(
        RequestContext<CallToolRequestParams> context,
        IProgress<ProgressNotificationValue> progress,
        [Description("The tool that wants to run")] string tool_name,
        [Description("The tool's input")] JsonObject input,
        CancellationToken cancellationToken,
        [Description("The id of the tool_use block")] string? tool_use_id = null)
    {
        var projectId = context.User?.FindFirstValue(GodModeAuthExtensions.ProjectIdClaim)
            ?? throw new McpException("The caller is not a project");
        var request = new PermissionPromptRequest(tool_name, JsonSerializer.SerializeToElement(input), tool_use_id);

        // Cancelled when claude cancels the call or its connection drops: the request is then withdrawn
        var answer = projects.RequestPermissionAsync(projectId, request, cancellationToken);
        for (var waited = 1; await Task.WhenAny(answer, Task.Delay(_keepAlive, CancellationToken.None)) != answer; waited++)
            progress.Report(new ProgressNotificationValue { Progress = waited, Message = "Waiting for the user" });
        return JsonSerializer.Serialize(await answer, JsonDefaults.Options);
    }
}
