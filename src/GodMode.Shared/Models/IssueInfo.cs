namespace GodMode.Shared.Models;

/// <summary>
/// An issue as a root's <c>issueInfo</c> script reports it (<see cref="Hubs.IProjectHub.DescribeIssue"/>): what voice
/// checks an action against before it reads a create back (#473). The server knows nothing of the VCS: the script does.
/// </summary>
/// <param name="Title">The issue's title; null when the script gave none.</param>
/// <param name="Labels">Its labels (<c>bug</c>, <c>feature</c>, <c>epic</c>…), as the script printed them.</param>
public record IssueInfo(string? Title, IReadOnlyList<string> Labels);
