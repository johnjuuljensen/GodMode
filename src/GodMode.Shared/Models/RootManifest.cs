namespace GodMode.Shared.Models;

/// <summary>
/// Manifest embedded in a .gmroot export package.
/// </summary>
public record RootManifest(
    string Name,
    string? Description = null,
    string? Author = null,
    string? Version = null,
    DateTime? ExportedAt = null,
    Dictionary<string, string>? ScriptHashes = null);

/// <summary>
/// Preview of a shared root before installation.
/// </summary>
public record SharedRootPreview(
    RootManifest Manifest,
    RootPreview Preview,
    string? Source = null);

/// <summary>
/// Tracks an installed shared root's origin for update detection.
/// </summary>
public record InstalledRootInfo(
    string RootName,
    string Source,
    string? GitUrl = null,
    string? GitRef = null,
    string? GitPath = null,
    DateTime InstalledAt = default,
    string? ManifestVersion = null);
