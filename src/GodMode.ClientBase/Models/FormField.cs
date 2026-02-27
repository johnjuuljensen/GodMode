using CommunityToolkit.Mvvm.ComponentModel;

namespace GodMode.ClientBase.Models;

/// <summary>
/// Represents a dynamic form field parsed from a JSON Schema inputSchema.
/// Used by CreateProjectViewModel to render dynamic forms.
/// </summary>
public partial class FormField : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// Field type: "string", "boolean", "enum"
    /// </summary>
    public required string FieldType { get; init; }

    public bool IsRequired { get; init; }
    public bool IsMultiline { get; init; }
    public string? Description { get; init; }
    public string? DefaultValue { get; init; }
    public List<EnumOption>? EnumOptions { get; init; }

    [ObservableProperty]
    private string _value = "";
}

public record EnumOption(string Value, string Label);
