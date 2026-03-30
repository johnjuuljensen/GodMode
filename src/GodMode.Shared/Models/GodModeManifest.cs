namespace GodMode.Shared.Models;

/// <summary>
/// Declares the complete desired state of a GodMode instance.
/// Used by the convergence engine to reconcile disk state.
/// </summary>
public record GodModeManifest(
    Dictionary<string, ManifestRoot>? Roots = null,
    Dictionary<string, ManifestProfile>? Profiles = null,
    ManifestSettings? Settings = null);

/// <summary>
/// A root entry in the manifest. Can be local or from a git source.
/// </summary>
public record ManifestRoot(
    string? Path = null,
    string? Git = null,
    string? Ref = null,
    string? GitPath = null);

/// <summary>
/// A profile entry in the manifest.
/// </summary>
public record ManifestProfile(
    string[]? Roots = null,
    string? Description = null,
    Dictionary<string, McpServerConfig>? McpServers = null,
    Dictionary<string, string>? Environment = null);

/// <summary>
/// Global manifest settings.
/// </summary>
public record ManifestSettings(
    string? ProjectRootsDir = null,
    string? Urls = null);
