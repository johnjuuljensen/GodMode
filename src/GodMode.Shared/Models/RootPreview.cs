namespace GodMode.Shared.Models;

/// <summary>
/// Preview of a root's files before writing to disk.
/// Used for both template instantiation and LLM-generated roots.
/// Each entry is a relative path within .godmode-root/ -> file content.
/// </summary>
public record RootPreview(
    Dictionary<string, string> Files,
    string? ValidationError = null
);
