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
/// Per-root provenance metadata stored in .godmode-root/source.json.
/// Self-describing — lives with the root, not in a centralized file.
/// </summary>
public record RootSourceInfo(
    string? Git = null,
    string? Ref = null,
    string? Path = null,
    DateTime InstalledAt = default,
    string? Version = null);
