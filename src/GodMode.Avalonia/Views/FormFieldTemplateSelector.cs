using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using GodMode.ClientBase.Models;

namespace GodMode.Avalonia.Views;

/// <summary>
/// Selects the appropriate template for a FormField based on its type.
/// </summary>
public class FormFieldTemplateSelector : IDataTemplate
{
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new();

    public Control Build(object? param)
    {
        if (param is FormField field && Templates.TryGetValue(GetTemplateKey(field), out var template))
            return template.Build(param)!;

        // Fallback: string template
        if (Templates.TryGetValue("string", out var fallback))
            return fallback.Build(param)!;

        return new TextBlock { Text = $"No template for field" };
    }

    public bool Match(object? data) => data is FormField;

    private static string GetTemplateKey(FormField field)
    {
        if (field.FieldType == "boolean") return "boolean";
        if (field.IsMultiline) return "multiline";
        return "string";
    }
}
