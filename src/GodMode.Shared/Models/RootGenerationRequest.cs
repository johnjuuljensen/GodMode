namespace GodMode.Shared.Models;

/// <summary>
/// Request to generate a root configuration using LLM inference.
/// </summary>
public record RootGenerationRequest(
    string Instruction,
    Dictionary<string, string>? CurrentFiles = null,
    string[]? SchemaFields = null);
