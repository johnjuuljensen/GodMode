namespace GodMode.Shared.Models;

/// <summary>
/// Preview of a root's file contents, used for create/update/import review.
/// Maps relative file paths to their text content.
/// </summary>
public record RootPreview(
    Dictionary<string, string> Files,
    string? ValidationError = null);
