using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.Shared;

/// <summary>
/// Canonical JSON serializer options for the GodMode protocol.
/// PascalCase properties, string enums, nullable fields omitted.
/// All C# projects should use these options when serializing shared types.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Standard options: PascalCase properties, string enums, indented output.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>
    /// Compact options: same as standard but without indentation.
    /// </summary>
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    private static JsonSerializerOptions Create(bool writeIndented = true) => new()
    {
        PropertyNamingPolicy = null, // PascalCase
        WriteIndented = writeIndented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
