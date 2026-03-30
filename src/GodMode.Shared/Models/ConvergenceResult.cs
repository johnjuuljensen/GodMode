namespace GodMode.Shared.Models;

/// <summary>
/// Result of a manifest convergence operation.
/// </summary>
public record ConvergenceResult(
    List<string> Actions,
    List<string> Errors,
    List<string> Warnings);
