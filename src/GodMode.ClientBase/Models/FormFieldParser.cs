using System.Text.Json;

namespace GodMode.ClientBase.Models;

/// <summary>
/// Parses a JSON Schema (subset) into FormField instances for dynamic form rendering.
/// Supports: string, string+enum (ComboBox), boolean, x-multiline extension.
/// </summary>
public static class FormFieldParser
{
    /// <summary>
    /// Parses an inputSchema JsonElement into a list of FormField instances.
    /// Returns default fields (name + prompt) when schema is null.
    /// </summary>
    public static List<FormField> Parse(JsonElement? inputSchema)
    {
        if (inputSchema == null || inputSchema.Value.ValueKind == JsonValueKind.Undefined)
            return GetDefaultFields();

        var schema = inputSchema.Value;
        var fields = new List<FormField>();

        if (!schema.TryGetProperty("properties", out var properties))
            return GetDefaultFields();

        // Get required fields
        var requiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (name != null) requiredSet.Add(name);
            }
        }

        foreach (var property in properties.EnumerateObject())
        {
            var key = property.Name;
            var prop = property.Value;

            var title = GetStringProp(prop, "title") ?? key;
            var description = GetStringProp(prop, "description");
            var defaultValue = GetStringProp(prop, "default");
            var type = GetStringProp(prop, "type") ?? "string";
            var isMultiline = prop.TryGetProperty("x-multiline", out var ml) && ml.GetBoolean();
            var isRequired = requiredSet.Contains(key);

            // Check for enum
            List<EnumOption>? enumOptions = null;
            string fieldType;

            if (prop.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
            {
                fieldType = "enum";
                enumOptions = [];
                foreach (var item in enumValues.EnumerateArray())
                {
                    var val = item.GetString() ?? "";
                    enumOptions.Add(new EnumOption(val, val));
                }
            }
            else if (type == "boolean")
            {
                fieldType = "boolean";
            }
            else
            {
                fieldType = "string";
            }

            fields.Add(new FormField
            {
                Key = key,
                Title = title,
                FieldType = fieldType,
                IsRequired = isRequired,
                IsMultiline = isMultiline,
                Description = description,
                DefaultValue = defaultValue,
                EnumOptions = enumOptions,
                Value = defaultValue ?? ""
            });
        }

        return fields;
    }

    /// <summary>
    /// Returns the default fields (name + prompt) when no schema is defined.
    /// </summary>
    public static List<FormField> GetDefaultFields()
    {
        return
        [
            new FormField
            {
                Key = "name",
                Title = "Project Name",
                FieldType = "string",
                IsRequired = true,
                IsMultiline = false,
                Description = "Name for the new project"
            },
            new FormField
            {
                Key = "prompt",
                Title = "Initial Prompt",
                FieldType = "string",
                IsRequired = true,
                IsMultiline = true,
                Description = "The initial task or prompt for Claude"
            }
        ];
    }

    private static string? GetStringProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
