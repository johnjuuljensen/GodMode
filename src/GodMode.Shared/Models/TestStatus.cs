namespace GodMode.Shared.Models;

/// <summary>
/// Test status information for a project.
/// </summary>
/// <param name="Total">The total number of tests.</param>
/// <param name="Passed">The number of tests that passed.</param>
/// <param name="Failed">The number of tests that failed.</param>
/// <param name="LastRun">The timestamp when tests were last run.</param>
public record TestStatus(
    int Total,
    int Passed,
    int Failed,
    DateTime? LastRun
);
