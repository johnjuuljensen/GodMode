namespace GodMode.Shared.Models;

public enum ChatResponseType
{
    Text,
    ToolCall,
    ToolResult,
    Error
}

public sealed record ChatResponseMessage(
    ChatResponseType Type,
    string Content,
    string? ToolName = null);
