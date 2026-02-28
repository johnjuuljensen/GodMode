namespace GodMode.AI.Tools;

public sealed class ToolParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; } = true;
}
