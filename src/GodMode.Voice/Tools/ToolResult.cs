namespace GodMode.Voice.Tools;

public sealed class ToolResult
{
    public required string ToolName { get; init; }
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    public static ToolResult Ok(string toolName, string output) =>
        new() { ToolName = toolName, Success = true, Output = output };

    public static ToolResult Fail(string toolName, string error) =>
        new() { ToolName = toolName, Success = false, Error = error };
}
