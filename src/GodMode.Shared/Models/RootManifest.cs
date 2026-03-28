namespace GodMode.Shared.Models;

/// <summary>
/// Metadata for a portable root package (.gmroot file).
/// Stored inside the package and also used for the community index.
/// </summary>
public record RootManifest(
    string Name,
    string DisplayName,
    string? Description = null,
    string? Author = null,
    string? Version = null,
    string[]? Platforms = null,
    string[]? Tags = null,
    string? Source = null,
    string? MinGodModeVersion = null
);

/// <summary>
/// Preview of a shared root before installation.
/// Includes the manifest, file listing, and script contents for security review.
/// </summary>
public record SharedRootPreview(
    RootManifest Manifest,
    Dictionary<string, string> Files,
    Dictionary<string, string>? ScriptHashes = null
);

/// <summary>
/// Tracks an installed shared root for update detection.
/// Stored in ProjectRootsDir/installed.json.
/// </summary>
public record InstalledRootInfo(
    string RootName,
    string Source,
    string? Version = null,
    string? CommitSha = null,
    DateTime InstalledAt = default,
    Dictionary<string, string>? ScriptHashes = null
);
