using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.Shared.Models;

namespace GodMode.Server.Models;

/// <summary>
/// A call claude made to the MCP <c>permission_prompt</c> tool, waiting for the user. It is a
/// <see cref="Permission"/> for any tool but AskUserQuestion, which is a <see cref="Question"/>.
/// It lives in memory only, for as long as claude's call does: a server restart ends it.
/// </summary>
public sealed class PendingRequest
{
    private static long _nextSequence;

    public PendingRequest(PendingPermission? permission, PendingQuestion? question, JsonElement input, PermissionDetail? detail = null)
    {
        Permission = permission;
        Question = question;
        Input = input;
        Detail = detail;
    }

    public string Id => Permission?.RequestId ?? Question!.RequestId;

    /// <summary>The order requests arrived in; the oldest is the one the status shows.</summary>
    public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);

    /// <summary>The <see cref="Sequence"/> of the latest request made so far; 0 before the first.</summary>
    public static long LastIssued => Interlocked.Read(ref _nextSequence);

    public PendingPermission? Permission { get; }
    public PendingQuestion? Question { get; }

    /// <summary>
    /// The tool's input as claude sent it, which an allow without an updated input runs with. It is
    /// the server's alone: <see cref="PendingPermission"/> does not carry it to clients or status.json.
    /// </summary>
    public JsonElement Input { get; }

    /// <summary>What a <see cref="Permission"/> would run, for the hub's GetPermissionDetail; null for a question.</summary>
    public PermissionDetail? Detail { get; }

    /// <summary>Completed with what the tool returns to claude.</summary>
    public TaskCompletionSource<PermissionPromptResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The arguments claude calls the MCP <c>permission_prompt</c> tool with.</summary>
/// <param name="ToolName">The tool claude wants to run.</param>
/// <param name="Input">The tool's input.</param>
/// <param name="ToolUseId">The id of claude's tool_use block.</param>
public record PermissionPromptRequest(string ToolName, JsonElement Input, string? ToolUseId);

/// <summary>
/// The answer the tool hands back to claude, as its JSON text: <c>{"behavior":"allow","updatedInput":{…}}</c>
/// or <c>{"behavior":"deny","message":"…"}</c>. claude runs the tool with <c>updatedInput</c>, not
/// with the input it asked with.
/// </summary>
public sealed record PermissionPromptResult(
    [property: JsonPropertyName("behavior")] string Behavior,
    [property: JsonPropertyName("updatedInput")] JsonElement? UpdatedInput = null,
    [property: JsonPropertyName("message")] string? Message = null)
{
    public static PermissionPromptResult Allow(JsonElement input) => new("allow", UpdatedInput: input);
    public static PermissionPromptResult Deny(string message) => new("deny", Message: message);
}
