namespace GodMode.Shared.Models;

/// <summary>
/// Request for LLM-assisted root generation or modification.
/// </summary>
public record RootGenerationRequest(
    /// <summary>User's natural-language description of what they want.</summary>
    string UserInstruction,

    /// <summary>Current root files (for modification). Null for new root generation.</summary>
    Dictionary<string, string>? CurrentFiles = null,

    /// <summary>Schema fields defined in the visual editor (so LLM can reference them in scripts).</summary>
    SchemaFieldDefinition[]? SchemaFields = null
);

/// <summary>
/// A field definition from the visual schema editor, passed to LLM for script generation.
/// </summary>
public record SchemaFieldDefinition(
    string Key,
    string Title,
    string FieldType,
    bool IsRequired = false,
    bool IsMultiline = false,
    string[]? EnumValues = null
);
