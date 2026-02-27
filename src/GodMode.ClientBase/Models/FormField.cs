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

    /// <summary>
    /// Boolean view of Value for checkbox/toggle binding.
    /// Syncs with Value as "true"/"false" strings.
    /// </summary>
    public bool BoolValue
    {
        get => Value is "true" or "True";
        set
        {
            Value = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    partial void OnValueChanged(string value)
    {
        if (FieldType == "boolean")
            OnPropertyChanged(nameof(BoolValue));
    }
}

public record EnumOption(string Value, string Label);
