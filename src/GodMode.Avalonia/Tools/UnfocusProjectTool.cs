using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class UnfocusProjectTool(VoiceContext context) : ITool
{
    public string Name => "unfocus_project";
    public string Description => "Stops watching the currently focused project's output.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        if (context.Focus is null)
            return Task.FromResult(ToolResult.Ok(Name, "No project is currently focused."));

        var name = context.Focus.ProjectName;
        context.UnfocusProject();
        return Task.FromResult(ToolResult.Ok(Name, $"Unfocused from '{name}'."));
    }
}
