namespace GodMode.Shared.Models;

/// <summary>One line of a project's output, as replayed from <c>output.jsonl</c>.</summary>
/// <param name="Offset">The byte offset in <c>output.jsonl</c> just after this line: subscribe from it to get only what follows.</param>
/// <param name="RawJson">The raw JSON line from Claude's --output-format stream-json.</param>
public record OutputLine(long Offset, string RawJson);
