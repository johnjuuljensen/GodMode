namespace GodMode.Shared.Models;

/// <summary>
/// Metrics about a project's Claude usage.
/// </summary>
/// <param name="InputTokens">The number of input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens generated.</param>
/// <param name="ToolCalls">The number of tool calls made.</param>
/// <param name="Duration">The total duration of Claude execution.</param>
/// <param name="CostEstimate">The estimated cost in USD.</param>
public record ProjectMetrics(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    TimeSpan Duration,
    decimal CostEstimate
);
