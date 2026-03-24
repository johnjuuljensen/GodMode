using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A bundled root template that users can select to create a new root.
/// Templates contain parameterized config/schema/script files.
/// </summary>
public record RootTemplate(
    string Name,
    string DisplayName,
    string Description,
    string? Icon = null,
    RootTemplateParameter[]? Parameters = null
);

/// <summary>
/// A parameter required by a template (e.g., "repoUrl", "branchConvention").
/// </summary>
public record RootTemplateParameter(
    string Key,
    string Title,
    string? Description = null,
    string? DefaultValue = null,
    bool Required = false
);
