# Agent Task: Root Creation UX and Sharing System

You are implementing two features for GodMode's root configuration system:

1. **Root Creation UX** — Visual schema editor, built-in templates, and LLM-assisted script generation so users can create new roots without hand-writing JSON and scripts
2. **Root Sharing** — Export/import system using portable `.gmroot` packages (ZIP archives), supporting file sharing, URL import, git import, and drag-and-drop

Read this entire document before starting. Follow the GodMode patterns described in `CLAUDE.md`.

---

## Architecture Overview

### What is a Root?

A "root" is a config-driven project template stored in a `.godmode-root/` directory. It defines how Claude Code projects are created, including input forms, scripts, environment variables, and model selection.

```
my-root/
├── .godmode-root/
│   ├── config.json              # Base config (description, env, claudeArgs, prepare/delete scripts)
│   ├── config.freeform.json     # Per-action overlay (merged with base)
│   ├── config.issue.json        # Per-action overlay
│   ├── freeform/
│   │   ├── schema.json          # JSON Schema → dynamic input form
│   │   └── create.sh / .ps1    # Action-specific creation script
│   ├── issue/
│   │   ├── schema.json
│   │   └── create.sh / .ps1
│   └── scripts/
│       ├── prepare.sh / .ps1   # One-time setup (e.g., clone bare repo)
│       └── delete.sh / .ps1    # Cleanup (e.g., remove worktree)
```

### How Roots are Discovered

`ProjectManager` scans `ProjectRootsDir` for subdirectories containing `.godmode-root/`. `RootConfigReader` reads and merges configs fresh on each operation (no caching). The UI gets `ProjectRootInfo[]` with actions and schemas via SignalR.

### Existing Patterns to Reuse

| Pattern | Location | Purpose |
|---------|----------|---------|
| `FormField` | `src/GodMode.ClientBase/Models/FormField.cs` | Observable form field model (Key, Title, FieldType, Value, etc.) |
| `FormFieldParser.Parse()` | `src/GodMode.ClientBase/Models/FormFieldParser.cs` | Parses JSON Schema → `List<FormField>` |
| `FormFieldTemplateSelector` | `src/GodMode.Avalonia/Views/FormFieldTemplateSelector.cs` | Selects Avalonia DataTemplate by field type |
| `CreateProjectView/ViewModel` | `src/GodMode.Avalonia/Views/CreateProjectView.axaml`, `ViewModels/CreateProjectViewModel.cs` | MVVM pattern: loads roots, actions, dynamic forms, creates projects |
| `RootConfigReader` | `src/GodMode.Server/Services/RootConfigReader.cs` | Multi-file config discovery, merging, schema loading |
| `ScriptRunner` | `src/GodMode.Server/Services/ScriptRunner.cs` | Cross-platform script execution (.sh/.ps1) |
| `InferenceRouter` | `src/GodMode.AI/InferenceRouter.cs` | Tier-based LLM routing with fallback chain |
| `MergeDictionaries` | `RootConfigReader.cs` line 254 | Dictionary merge (overlay wins) — pattern for all merges |

---

## Feature 1: Root Creation UX

### Step 1: Shared Models

#### 1a. `src/GodMode.Shared/Models/RootTemplate.cs` (NEW)

```csharp
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
```

#### 1b. `src/GodMode.Shared/Models/RootPreview.cs` (NEW)

```csharp
namespace GodMode.Shared.Models;

/// <summary>
/// Preview of a root's files before writing to disk.
/// Used for both template instantiation and LLM-generated roots.
/// Each entry is a relative path within .godmode-root/ → file content.
/// </summary>
public record RootPreview(
    Dictionary<string, string> Files,
    string? ValidationError = null
);
```

#### 1c. `src/GodMode.Shared/Models/RootGenerationRequest.cs` (NEW)

```csharp
using System.Text.Json;

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
```

### Step 2: Built-in Templates

#### `src/GodMode.Server/Templates/` (NEW directory)

Create template files as embedded resources. Each template is a directory containing a `template.json` manifest and file templates.

**Template manifest format** (`template.json`):
```json
{
  "name": "git-worktree",
  "displayName": "Git Worktree Projects",
  "description": "Creates projects as git worktrees from a bare repository clone",
  "icon": "git",
  "parameters": [
    { "key": "repoUrl", "title": "Repository URL", "description": "HTTPS or SSH URL of the git repository", "required": true },
    { "key": "profileName", "title": "Profile Name", "description": "Name for the GodMode profile", "defaultValue": "Development" }
  ]
}
```

**Template file format**: Files use `{{paramKey}}` placeholders that get replaced with user-provided parameter values.

#### Templates to ship:

**1. `blank/`** — Empty root, just a basic config:
- `template.json` (no parameters)
- `config.json.tmpl`: `{ "description": "{{description}}" }`

**2. `ad-hoc/`** — No VCS, folder with Claude:
- `template.json` (no parameters)
- `config.json.tmpl`: `{ "description": "Ad-hoc tasks — just a folder with Claude, no VCS", "model": "sonnet" }`

**3. `git-worktree/`** — Bare clone + worktrees:
- `template.json` (parameters: repoUrl, profileName)
- `config.json.tmpl`, `config.freeform.json.tmpl`, `freeform/schema.json.tmpl`
- `freeform/create.sh.tmpl`, `freeform/create.ps1.tmpl`
- `scripts/prepare.sh.tmpl`, `scripts/prepare.ps1.tmpl`
- `scripts/delete.sh.tmpl`, `scripts/delete.ps1.tmpl`
- Based on the existing `godmode-dev` root at `.devcontainer/godmode-server/roots/godmode-dev/.godmode-root/`, replacing hardcoded repo URLs with `{{repoUrl}}`

**4. `git-clone/`** — Clone a repo per project:
- `template.json` (parameters: repoUrl)
- `config.json.tmpl`, `freeform/schema.json.tmpl`
- `freeform/create.sh.tmpl`, `freeform/create.ps1.tmpl` (simple `git clone`)
- `scripts/delete.sh.tmpl`, `scripts/delete.ps1.tmpl` (rm -rf)

**5. `local-folder/`** — Work in an existing local directory:
- `template.json` (no parameters)
- `config.json.tmpl`: points to local path via schema input
- `schema.json.tmpl`: adds `path` field of type string

#### Embed templates in the server project

In `GodMode.Server.csproj`, add:
```xml
<ItemGroup>
  <EmbeddedResource Include="Templates\**\*" />
</ItemGroup>
```

### Step 3: Template Service

#### `src/GodMode.Server/Services/RootTemplateService.cs` (NEW)

```csharp
using System.Reflection;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads bundled root templates from embedded resources.
/// Instantiates templates by replacing {{param}} placeholders with user values.
/// </summary>
public class RootTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Lists all available templates.
    /// </summary>
    public RootTemplate[] ListTemplates()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "GodMode.Server.Templates.";
        var templateNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".template.json"))
            .Select(n => n[prefix.Length..^".template.json".Length].Split('.')[0])
            .Distinct()
            .ToArray();

        return templateNames
            .Select(name => LoadTemplateManifest(assembly, prefix, name))
            .Where(t => t != null)
            .Cast<RootTemplate>()
            .ToArray();
    }

    /// <summary>
    /// Instantiates a template with user-provided parameter values.
    /// Returns a RootPreview with all files ready to write.
    /// </summary>
    public RootPreview InstantiateTemplate(string templateName, Dictionary<string, string> parameters)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = $"GodMode.Server.Templates.{templateName}.";

        var files = new Dictionary<string, string>();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".tmpl"));

        foreach (var resourceName in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            // Replace {{param}} placeholders
            foreach (var (key, value) in parameters)
                content = content.Replace($"{{{{{key}}}}}", value);

            // Convert resource name to file path: remove prefix and .tmpl suffix
            var relativePath = resourceName[prefix.Length..^".tmpl".Length]
                .Replace('.', Path.DirectorySeparatorChar);

            // Fix: the last segment before extension needs its dot back
            // e.g., "config.json" was encoded as "config.json.tmpl"
            // Resource embedding flattens paths, so we need a naming convention
            // Use "__" as directory separator in resource names
            relativePath = relativePath.Replace("__", Path.DirectorySeparatorChar.ToString());

            files[relativePath] = content;
        }

        return new RootPreview(files);
    }

    private static RootTemplate? LoadTemplateManifest(Assembly assembly, string prefix, string name)
    {
        var resourceName = $"{prefix}{name}.template.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        return JsonSerializer.Deserialize<RootTemplate>(stream, JsonOptions);
    }
}
```

**Note on embedded resource naming**: .NET embedded resources flatten directory paths using dots. To preserve directory structure in template files, use `__` as a directory separator in filenames. For example: `freeform__create.sh.tmpl` → `freeform/create.sh`. The service handles this conversion.

### Step 4: Root Creator Service

#### `src/GodMode.Server/Services/RootCreator.cs` (NEW)

```csharp
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Writes .godmode-root/ directory structure from a RootPreview.
/// Validates that the generated config is parseable by RootConfigReader.
/// </summary>
public class RootCreator
{
    private readonly IRootConfigReader _configReader;
    private readonly ILogger<RootCreator> _logger;

    public RootCreator(IRootConfigReader configReader, ILogger<RootCreator> logger)
    {
        _configReader = configReader;
        _logger = logger;
    }

    /// <summary>
    /// Writes root files from a preview to the target directory.
    /// Creates the .godmode-root/ subdirectory structure.
    /// Sets execute permissions on script files (Unix).
    /// </summary>
    public void WriteRoot(string rootPath, RootPreview preview)
    {
        var godmodeRootPath = Path.Combine(rootPath, ".godmode-root");
        Directory.CreateDirectory(godmodeRootPath);

        foreach (var (relativePath, content) in preview.Files)
        {
            var fullPath = Path.Combine(godmodeRootPath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null) Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);

            // Set execute permission on script files (Unix)
            if (relativePath.EndsWith(".sh") && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(fullPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set execute permission on {Path}", fullPath);
                }
            }
        }

        _logger.LogInformation("Wrote {FileCount} root files to {Path}", preview.Files.Count, godmodeRootPath);
    }

    /// <summary>
    /// Validates a RootPreview by checking that config.json is parseable.
    /// Returns a validation error message, or null if valid.
    /// </summary>
    public string? Validate(RootPreview preview)
    {
        if (!preview.Files.ContainsKey("config.json"))
            return "Root must contain a config.json file";

        try
        {
            // Try parsing config.json
            JsonSerializer.Deserialize<JsonElement>(preview.Files["config.json"]);
        }
        catch (JsonException ex)
        {
            return $"config.json is not valid JSON: {ex.Message}";
        }

        // Validate any schema.json files
        foreach (var (path, content) in preview.Files)
        {
            if (!path.EndsWith("schema.json")) continue;
            try
            {
                var schema = JsonSerializer.Deserialize<JsonElement>(content);
                if (!schema.TryGetProperty("type", out _))
                    return $"{path}: JSON Schema must have a 'type' property";
            }
            catch (JsonException ex)
            {
                return $"{path} is not valid JSON: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an existing root's files into a RootPreview for editing.
    /// </summary>
    public RootPreview ReadExistingRoot(string rootPath)
    {
        var godmodeRootPath = Path.Combine(rootPath, ".godmode-root");
        var files = new Dictionary<string, string>();

        if (!Directory.Exists(godmodeRootPath))
            return new RootPreview(files);

        foreach (var filePath in Directory.GetFiles(godmodeRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(godmodeRootPath, filePath);
            // Skip binary files, only include text-based config/schema/script files
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".json" or ".sh" or ".ps1" or ".cmd" or ".bat" or ".md" or ".txt" or "")
            {
                files[relativePath] = File.ReadAllText(filePath);
            }
        }

        return new RootPreview(files);
    }
}
```

### Step 5: Visual Schema Editor

The schema editor lets users visually build `schema.json` files by adding, editing, and reordering form fields.

#### 5a. `src/GodMode.Avalonia/ViewModels/SchemaEditorViewModel.cs` (NEW)

```csharp
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Models;

namespace GodMode.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the visual schema editor.
/// Users add/edit/reorder form fields. The editor serializes to JSON Schema.
/// Each field in the editor is a SchemaFieldItem (editable metadata + FormField for preview).
/// </summary>
public partial class SchemaEditorViewModel : ViewModelBase
{
    public ObservableCollection<SchemaFieldItem> Fields { get; } = new();

    [ObservableProperty]
    private SchemaFieldItem? _selectedField;

    /// <summary>
    /// Live preview fields (generated from current editor state for the form preview panel).
    /// </summary>
    public ObservableCollection<FormField> PreviewFields { get; } = new();

    public static string[] FieldTypes { get; } = ["string", "multiline", "boolean", "enum"];

    [RelayCommand]
    private void AddField()
    {
        var field = new SchemaFieldItem
        {
            Key = $"field{Fields.Count + 1}",
            Title = $"Field {Fields.Count + 1}",
            FieldType = "string"
        };
        Fields.Add(field);
        SelectedField = field;
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveField(SchemaFieldItem field)
    {
        Fields.Remove(field);
        if (SelectedField == field) SelectedField = Fields.LastOrDefault();
        RefreshPreview();
    }

    [RelayCommand]
    private void MoveFieldUp(SchemaFieldItem field)
    {
        var index = Fields.IndexOf(field);
        if (index > 0) Fields.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveFieldDown(SchemaFieldItem field)
    {
        var index = Fields.IndexOf(field);
        if (index < Fields.Count - 1) Fields.Move(index, index + 1);
    }

    /// <summary>
    /// Serializes the current fields to a JSON Schema string.
    /// Output is compatible with FormFieldParser.Parse().
    /// </summary>
    public string SerializeToJsonSchema()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var field in Fields)
        {
            var prop = new Dictionary<string, object>
            {
                ["type"] = field.FieldType is "enum" ? "string" : (field.FieldType is "boolean" ? "boolean" : "string"),
                ["title"] = field.Title
            };

            if (!string.IsNullOrEmpty(field.Description))
                prop["description"] = field.Description;

            if (!string.IsNullOrEmpty(field.DefaultValue))
                prop["default"] = field.DefaultValue;

            if (field.FieldType == "multiline")
                prop["x-multiline"] = true;

            if (field.FieldType == "enum" && field.EnumValues is { Count: > 0 })
                prop["enum"] = field.EnumValues.ToArray();

            properties[field.Key] = prop;

            if (field.IsRequired)
                required.Add(field.Key);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
            schema["required"] = required;

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Loads fields from an existing JSON Schema string (for editing).
    /// </summary>
    public void LoadFromJsonSchema(string jsonSchema)
    {
        Fields.Clear();
        var schemaElement = JsonSerializer.Deserialize<JsonElement>(jsonSchema);
        var formFields = FormFieldParser.Parse(schemaElement);

        foreach (var ff in formFields)
        {
            Fields.Add(new SchemaFieldItem
            {
                Key = ff.Key,
                Title = ff.Title,
                FieldType = ff.IsMultiline ? "multiline" : ff.FieldType,
                IsRequired = ff.IsRequired,
                Description = ff.Description,
                DefaultValue = ff.DefaultValue,
                EnumValues = ff.EnumOptions != null
                    ? new ObservableCollection<string>(ff.EnumOptions.Select(e => e.Value))
                    : null
            });
        }

        RefreshPreview();
    }

    /// <summary>
    /// Returns SchemaFieldDefinition[] for passing to LLM generation.
    /// </summary>
    public SchemaFieldDefinition[] GetFieldDefinitions() =>
        Fields.Select(f => new SchemaFieldDefinition(
            f.Key, f.Title, f.FieldType, f.IsRequired, f.FieldType == "multiline",
            f.EnumValues?.ToArray()
        )).ToArray();

    public void RefreshPreview()
    {
        PreviewFields.Clear();
        var schema = SerializeToJsonSchema();
        var schemaElement = JsonSerializer.Deserialize<JsonElement>(schema);
        foreach (var field in FormFieldParser.Parse(schemaElement))
            PreviewFields.Add(field);
    }
}

/// <summary>
/// A single field being edited in the schema editor.
/// </summary>
public partial class SchemaFieldItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _fieldType = "string";
    [ObservableProperty] private bool _isRequired;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _defaultValue;
    [ObservableProperty] private ObservableCollection<string>? _enumValues;
}
```

#### 5b. `src/GodMode.Avalonia/Views/SchemaEditorView.axaml` (NEW)

Layout:
- **Left panel**: List of fields with up/down/remove buttons and "Add Field" button at bottom
- **Center panel**: Edit selected field (Key, Title, Type dropdown, Required toggle, Description, Default, enum values editor)
- **Right panel**: Live form preview using `FormFieldTemplateSelector` and `PreviewFields`

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:GodMode.Avalonia.ViewModels"
             xmlns:views="using:GodMode.Avalonia.Views"
             xmlns:models="using:GodMode.ClientBase.Models"
             x:Class="GodMode.Avalonia.Views.SchemaEditorView"
             x:DataType="vm:SchemaEditorViewModel">

    <UserControl.Resources>
        <views:FormFieldTemplateSelector x:Key="PreviewFieldSelector">
            <!-- Reuse same templates as CreateProjectView -->
            <DataTemplate x:Key="string" x:DataType="models:FormField">
                <StackPanel Spacing="5" Margin="0,5">
                    <TextBlock Text="{Binding Title}" FontWeight="Bold" FontSize="12" />
                    <TextBox Text="{Binding Value}" Watermark="{Binding Description}" IsEnabled="False" />
                </StackPanel>
            </DataTemplate>
            <DataTemplate x:Key="multiline" x:DataType="models:FormField">
                <StackPanel Spacing="5" Margin="0,5">
                    <TextBlock Text="{Binding Title}" FontWeight="Bold" FontSize="12" />
                    <TextBox Watermark="{Binding Description}" AcceptsReturn="True" Height="80" IsEnabled="False" />
                </StackPanel>
            </DataTemplate>
            <DataTemplate x:Key="boolean" x:DataType="models:FormField">
                <StackPanel Orientation="Horizontal" Spacing="10" Margin="0,8">
                    <ToggleSwitch IsEnabled="False" />
                    <TextBlock Text="{Binding Title}" VerticalAlignment="Center" FontSize="12" />
                </StackPanel>
            </DataTemplate>
        </views:FormFieldTemplateSelector>
    </UserControl.Resources>

    <Grid ColumnDefinitions="200,*,250" Margin="0">
        <!-- Left: Field list -->
        <Border Grid.Column="0" BorderBrush="{DynamicResource BorderSubtleBrush}" BorderThickness="0,0,1,0" Padding="8">
            <DockPanel>
                <Button DockPanel.Dock="Bottom" Content="+ Add Field" Command="{Binding AddFieldCommand}"
                        HorizontalAlignment="Stretch" Margin="0,8,0,0" FontSize="12" />
                <TextBlock DockPanel.Dock="Top" Text="FIELDS" FontSize="10" FontWeight="SemiBold"
                           LetterSpacing="1" Foreground="{DynamicResource TextTertiaryBrush}" Margin="0,0,0,8" />
                <ListBox ItemsSource="{Binding Fields}" SelectedItem="{Binding SelectedField}">
                    <ListBox.ItemTemplate>
                        <DataTemplate x:DataType="vm:SchemaFieldItem">
                            <Grid ColumnDefinitions="*,Auto,Auto,Auto">
                                <TextBlock Grid.Column="0" Text="{Binding Title}" FontSize="12"
                                           VerticalAlignment="Center" />
                                <Button Grid.Column="1" Content="^" FontSize="10" Padding="4,2"
                                        Command="{Binding $parent[ListBox].((vm:SchemaEditorViewModel)DataContext).MoveFieldUpCommand}"
                                        CommandParameter="{Binding}" />
                                <Button Grid.Column="2" Content="v" FontSize="10" Padding="4,2"
                                        Command="{Binding $parent[ListBox].((vm:SchemaEditorViewModel)DataContext).MoveFieldDownCommand}"
                                        CommandParameter="{Binding}" />
                                <Button Grid.Column="3" Content="x" FontSize="10" Padding="4,2"
                                        Command="{Binding $parent[ListBox].((vm:SchemaEditorViewModel)DataContext).RemoveFieldCommand}"
                                        CommandParameter="{Binding}" />
                            </Grid>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </DockPanel>
        </Border>

        <!-- Center: Edit selected field -->
        <ScrollViewer Grid.Column="1" Padding="16" IsVisible="{Binding SelectedField, Converter={StaticResource IsNotNullConverter}}">
            <StackPanel Spacing="12" DataContext="{Binding SelectedField}">
                <TextBlock Text="EDIT FIELD" FontSize="10" FontWeight="SemiBold"
                           LetterSpacing="1" Foreground="{DynamicResource TextTertiaryBrush}" />

                <StackPanel Spacing="4">
                    <TextBlock Text="Key (variable name)" FontSize="11" FontWeight="SemiBold" />
                    <TextBox Text="{Binding Key}" FontSize="13" />
                </StackPanel>

                <StackPanel Spacing="4">
                    <TextBlock Text="Title (display label)" FontSize="11" FontWeight="SemiBold" />
                    <TextBox Text="{Binding Title}" FontSize="13" />
                </StackPanel>

                <StackPanel Spacing="4">
                    <TextBlock Text="Type" FontSize="11" FontWeight="SemiBold" />
                    <ComboBox ItemsSource="{Binding $parent[UserControl].((vm:SchemaEditorViewModel)DataContext).FieldTypes}"
                              SelectedItem="{Binding FieldType}" FontSize="13" HorizontalAlignment="Stretch" />
                </StackPanel>

                <StackPanel Orientation="Horizontal" Spacing="10">
                    <ToggleSwitch IsChecked="{Binding IsRequired}" />
                    <TextBlock Text="Required" VerticalAlignment="Center" FontSize="12" />
                </StackPanel>

                <StackPanel Spacing="4">
                    <TextBlock Text="Description (helper text)" FontSize="11" FontWeight="SemiBold" />
                    <TextBox Text="{Binding Description}" FontSize="13" />
                </StackPanel>

                <StackPanel Spacing="4">
                    <TextBlock Text="Default Value" FontSize="11" FontWeight="SemiBold" />
                    <TextBox Text="{Binding DefaultValue}" FontSize="13" />
                </StackPanel>

                <!-- Enum values (only shown for enum type) -->
                <!-- TODO: Add enum value editor when FieldType == "enum" -->
            </StackPanel>
        </ScrollViewer>

        <!-- Right: Live preview -->
        <Border Grid.Column="2" BorderBrush="{DynamicResource BorderSubtleBrush}" BorderThickness="1,0,0,0" Padding="12">
            <DockPanel>
                <TextBlock DockPanel.Dock="Top" Text="FORM PREVIEW" FontSize="10" FontWeight="SemiBold"
                           LetterSpacing="1" Foreground="{DynamicResource TextTertiaryBrush}" Margin="0,0,0,8" />
                <ItemsControl ItemsSource="{Binding PreviewFields}"
                              ItemTemplate="{StaticResource PreviewFieldSelector}" />
            </DockPanel>
        </Border>
    </Grid>
</UserControl>
```

Create the code-behind `SchemaEditorView.axaml.cs` with standard InitializeComponent().

### Step 6: LLM-Assisted Script Generation

#### 6a. Add GodMode.AI reference to GodMode.Server

In `src/GodMode.Server/GodMode.Server.csproj`, add:
```xml
<ProjectReference Include="..\GodMode.AI\GodMode.AI.csproj" />
```

In `src/GodMode.Server/Program.cs`, add after existing service registrations:
```csharp
using GodMode.AI;
// ...
builder.Services.AddGodModeAIServices();
```

**Important**: `InferenceRouter` is currently only used client-side. Adding it to the server enables LLM-assisted root generation. It works without an API key (gracefully returns empty), so this doesn't break existing deployments.

#### 6b. `src/GodMode.Server/Services/RootGenerationService.cs` (NEW)

```csharp
using System.Text.Json;
using GodMode.AI;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Orchestrates LLM-assisted root generation and modification.
/// Uses InferenceRouter to call the LLM with root-specific system prompts.
/// </summary>
public class RootGenerationService
{
    private readonly InferenceRouter _inference;
    private readonly ILogger<RootGenerationService> _logger;
    private bool _initialized;

    private const string SystemPrompt = """
        You are an expert at creating GodMode root configurations. A "root" defines how Claude Code projects are created.

        ## Root Structure
        A root lives in a `.godmode-root/` directory and contains:
        - `config.json` — Base config with: description, profileName, environment (dict), claudeArgs (string[]), prepare (script path), delete (script path), model
        - `config.{actionName}.json` — Per-action overlay. Merged with base: scalars replace, dicts merge, claudeArgs concatenate. Contains: description, model, scriptsCreateFolder, create (script path), nameTemplate, promptTemplate
        - `{actionName}/schema.json` — JSON Schema for the action's input form. Supported field types: string, string with x-multiline:true, boolean, enum. Required fields in "required" array.
        - `{actionName}/create.sh` and `{actionName}/create.ps1` — Cross-platform create scripts
        - `scripts/prepare.sh` and `scripts/prepare.ps1` — One-time setup scripts
        - `scripts/delete.sh` and `scripts/delete.ps1` — Cleanup scripts

        ## Script Environment Variables
        Scripts receive these environment variables:
        - GODMODE_ROOT_PATH — Root directory path
        - GODMODE_PROJECT_PATH — Project directory path
        - GODMODE_PROJECT_ID — Unique project ID
        - GODMODE_PROJECT_NAME — User-entered project name
        - GODMODE_RESULT_FILE — Path to result file (scripts can override project_path and project_name)
        - GODMODE_INPUT_{KEY} — Form input values (camelCase key → UPPER_SNAKE_CASE, e.g., repoUrl → GODMODE_INPUT_REPO_URL)
        - GODMODE_FORCE — "true" when force-deleting

        ## Templates
        - nameTemplate: Uses {fieldName} placeholders, e.g., "issue_{issueNumber}"
        - promptTemplate: Uses {fieldName} placeholders, e.g., "Read issue #{issueNumber} and implement"

        ## Script Conventions
        - Always provide BOTH .sh and .ps1 versions
        - .sh scripts: Start with #!/bin/bash and set -e
        - .ps1 scripts: Start with $ErrorActionPreference = 'Stop'
        - Use $env:VAR_NAME in PowerShell, $VAR_NAME in bash
        - Scripts are run from the ROOT directory, not the project directory
        - Script paths in config.json are relative to .godmode-root/ (e.g., "create": "freeform/create")
        - Script references are extensionless — the runtime resolves .sh or .ps1 based on platform

        ## Your Response Format
        Return a JSON object where keys are file paths (relative to .godmode-root/) and values are file contents.
        Example:
        ```json
        {
          "config.json": "{ ... }",
          "config.freeform.json": "{ ... }",
          "freeform/schema.json": "{ ... }",
          "freeform/create.sh": "#!/bin/bash\nset -e\n...",
          "freeform/create.ps1": "$ErrorActionPreference = 'Stop'\n..."
        }
        ```

        Return ONLY the JSON object, no markdown fences, no explanation.
        """;

    public RootGenerationService(InferenceRouter inference, ILogger<RootGenerationService> logger)
    {
        _inference = inference;
        _logger = logger;
    }

    /// <summary>
    /// Generates or modifies root files using LLM assistance.
    /// </summary>
    public async Task<RootPreview> GenerateAsync(RootGenerationRequest request, CancellationToken ct = default)
    {
        if (!_initialized)
        {
            await _inference.InitializeAsync();
            _initialized = true;
        }

        if (!_inference.IsLoaded)
        {
            return new RootPreview(new Dictionary<string, string>(),
                "No inference provider available. Configure an API key in ~/.godmode/inference.json");
        }

        var userMessage = BuildUserMessage(request);

        _logger.LogInformation("Generating root config via LLM ({Provider})", _inference.LastUsedProvider ?? "unknown");
        var response = await _inference.GenerateAsync(InferenceTier.Heavy, SystemPrompt, userMessage, ct);

        if (string.IsNullOrWhiteSpace(response))
        {
            return new RootPreview(new Dictionary<string, string>(),
                "LLM returned empty response. Check inference configuration.");
        }

        try
        {
            // Strip markdown fences if present
            response = response.Trim();
            if (response.StartsWith("```"))
            {
                var firstNewline = response.IndexOf('\n');
                response = response[(firstNewline + 1)..];
                if (response.EndsWith("```"))
                    response = response[..^3];
                response = response.Trim();
            }

            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(response);
            if (files == null || files.Count == 0)
                return new RootPreview(new Dictionary<string, string>(), "LLM response did not contain valid file definitions");

            return new RootPreview(files);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON");
            return new RootPreview(new Dictionary<string, string>(),
                $"Failed to parse LLM response: {ex.Message}");
        }
    }

    private static string BuildUserMessage(RootGenerationRequest request)
    {
        var parts = new List<string>();

        if (request.CurrentFiles is { Count: > 0 })
        {
            parts.Add("Here are the current root files:\n");
            foreach (var (path, content) in request.CurrentFiles)
                parts.Add($"--- {path} ---\n{content}\n");
            parts.Add("\nModify these files based on the following instruction:");
        }
        else
        {
            parts.Add("Create a new root configuration from scratch based on the following instruction:");
        }

        parts.Add(request.UserInstruction);

        if (request.SchemaFields is { Length: > 0 })
        {
            parts.Add("\nThe user has already defined these input form fields in the schema editor:");
            foreach (var field in request.SchemaFields)
            {
                var desc = $"- {field.Key} ({field.FieldType}): \"{field.Title}\"";
                if (field.IsRequired) desc += " [required]";
                if (field.EnumValues is { Length: > 0 }) desc += $" options: [{string.Join(", ", field.EnumValues)}]";
                parts.Add(desc);
            }
            parts.Add("Use these fields in the schema.json and reference them in scripts as GODMODE_INPUT_{UPPER_SNAKE_CASE_KEY}.");
        }

        return string.Join("\n", parts);
    }
}
```

### Step 7: Create Root View and ViewModel

#### 7a. `src/GodMode.Avalonia/ViewModels/CreateRootViewModel.cs` (NEW)

```csharp
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Models;
using GodMode.Shared.Models;

namespace GodMode.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the root creation flow.
/// Manages template selection, schema editing, LLM script generation, and saving.
/// </summary>
public partial class CreateRootViewModel : ViewModelBase
{
    private readonly IProjectService _projectService;

    public event Action? Completed;

    // Step tracking
    [ObservableProperty] private int _currentStep; // 0=template, 1=schema, 2=config, 3=scripts, 4=preview
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _hostId = "";

    // Template selection
    public ObservableCollection<RootTemplate> Templates { get; } = new();
    [ObservableProperty] private RootTemplate? _selectedTemplate;

    // Root metadata
    [ObservableProperty] private string _rootName = "";
    [ObservableProperty] private string _rootDescription = "";
    [ObservableProperty] private string _rootModel = "sonnet";

    // Template parameters
    public ObservableCollection<FormField> TemplateParameterFields { get; } = new();

    // Schema editor
    public SchemaEditorViewModel SchemaEditor { get; } = new();

    // LLM generation
    [ObservableProperty] private string _llmInstruction = "";

    // Preview
    public ObservableCollection<FilePreviewItem> PreviewFiles { get; } = new();

    // The preview result from the server
    private RootPreview? _currentPreview;

    public static string[] ModelPresets { get; } = ["opus", "opus[1m]", "sonnet", "sonnet[1m]", "haiku"];

    public CreateRootViewModel(INavigationService navigationService, IProjectService projectService)
        : base(navigationService)
    {
        _projectService = projectService;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var templates = await _projectService.ListRootTemplatesAsync(ProfileName, HostId);
            Templates.Clear();
            foreach (var t in templates)
                Templates.Add(t);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load templates: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectTemplateAsync(RootTemplate? template)
    {
        SelectedTemplate = template;
        TemplateParameterFields.Clear();

        if (template?.Parameters != null)
        {
            foreach (var p in template.Parameters)
            {
                TemplateParameterFields.Add(new FormField
                {
                    Key = p.Key,
                    Title = p.Title,
                    FieldType = "string",
                    IsRequired = p.Required,
                    Description = p.Description,
                    DefaultValue = p.DefaultValue,
                    Value = p.DefaultValue ?? ""
                });
            }
        }

        // If template has no parameters, go straight to schema editor
        if (TemplateParameterFields.Count == 0)
            CurrentStep = 1;
        else
            CurrentStep = 0; // Show template parameters
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < 4) CurrentStep++;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0) CurrentStep--;
    }

    /// <summary>
    /// Ask the LLM to generate scripts based on the schema fields and user instruction.
    /// </summary>
    [RelayCommand]
    private async Task GenerateWithLlmAsync()
    {
        if (string.IsNullOrWhiteSpace(LlmInstruction))
        {
            ErrorMessage = "Please describe what you want the scripts to do";
            return;
        }

        IsGenerating = true;
        ErrorMessage = null;

        try
        {
            var request = new RootGenerationRequest(
                UserInstruction: LlmInstruction,
                CurrentFiles: _currentPreview?.Files,
                SchemaFields: SchemaEditor.GetFieldDefinitions()
            );

            var preview = await _projectService.GenerateRootWithLlmAsync(ProfileName, HostId, request);

            if (preview.ValidationError != null)
            {
                ErrorMessage = preview.ValidationError;
                return;
            }

            _currentPreview = preview;
            RefreshPreviewFiles();
            CurrentStep = 4; // Jump to preview
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Generation failed: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    /// Build the final preview from schema editor + config + any LLM-generated scripts.
    /// </summary>
    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        ErrorMessage = null;

        var files = new Dictionary<string, string>();

        // config.json
        var config = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(RootDescription))
            config["description"] = RootDescription;
        if (!string.IsNullOrWhiteSpace(RootModel))
            config["model"] = RootModel;
        files["config.json"] = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // schema.json from editor
        if (SchemaEditor.Fields.Count > 0)
            files["schema.json"] = SchemaEditor.SerializeToJsonSchema();

        // Merge any LLM-generated files (scripts, action configs)
        if (_currentPreview?.Files != null)
        {
            foreach (var (path, content) in _currentPreview.Files)
            {
                if (!files.ContainsKey(path)) // Don't overwrite user-edited config/schema
                    files[path] = content;
            }
        }

        _currentPreview = new RootPreview(files);
        RefreshPreviewFiles();
        CurrentStep = 4;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(RootName))
        {
            ErrorMessage = "Please enter a root name";
            return;
        }

        if (_currentPreview == null || _currentPreview.Files.Count == 0)
        {
            ErrorMessage = "No files to save. Build preview first.";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            await _projectService.CreateRootAsync(ProfileName, HostId, RootName, _currentPreview);
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save root: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke();

    private void RefreshPreviewFiles()
    {
        PreviewFiles.Clear();
        if (_currentPreview?.Files == null) return;
        foreach (var (path, content) in _currentPreview.Files.OrderBy(f => f.Key))
            PreviewFiles.Add(new FilePreviewItem(path, content));
    }
}

public record FilePreviewItem(string Path, string Content);
```

#### 7b. `src/GodMode.Avalonia/Views/CreateRootView.axaml` (NEW)

A multi-step view. Implement as a single UserControl with step visibility bound to `CurrentStep`. Include:

- **Step 0**: Template gallery (cards) + parameter form + "Next" button
- **Step 1**: Schema editor (embed `SchemaEditorView`) + "Next"/"Back" buttons
- **Step 2**: Config editor (name, description, model) + "Next"/"Back"
- **Step 3**: LLM script generation (text input + "Generate" button) + "Build Preview"/"Back"
- **Step 4**: File preview (list of files with content) + "Save"/"Back"

Follow the styling patterns from `CreateProjectView.axaml`: same border styles, font sizes, spacing, button patterns.

### Step 8: SignalR Hub Methods for Root Creation

#### 8a. Add to `IProjectHub` (`src/GodMode.Shared/Hubs/IProjectHub.cs`)

```csharp
// Root Creation
Task<RootTemplate[]> ListRootTemplates();
Task<RootPreview> PreviewRootFromTemplate(string templateName, Dictionary<string, string> parameters);
Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request);
Task CreateRoot(string profileName, string rootName, RootPreview preview);
Task<RootPreview> GetRootPreview(string profileName, string rootName);
Task UpdateRoot(string profileName, string rootName, RootPreview preview);
```

#### 8b. Implement in `ProjectHub` (`src/GodMode.Server/Hubs/ProjectHub.cs`)

Follow existing delegation pattern — each method logs and delegates to `IProjectManager`:

```csharp
public async Task<RootTemplate[]> ListRootTemplates()
{
    _logger.LogInformation("Client {ConnectionId} requested root templates", Context.ConnectionId);
    return await _projectManager.ListRootTemplatesAsync();
}

public async Task<RootPreview> PreviewRootFromTemplate(string templateName, Dictionary<string, string> parameters)
{
    _logger.LogInformation("Client {ConnectionId} previewing template '{Template}'",
        Context.ConnectionId, templateName);
    return await _projectManager.PreviewRootFromTemplateAsync(templateName, parameters);
}

public async Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request)
{
    _logger.LogInformation("Client {ConnectionId} generating root with LLM",
        Context.ConnectionId);
    return await _projectManager.GenerateRootWithLlmAsync(request);
}

public async Task CreateRoot(string profileName, string rootName, RootPreview preview)
{
    _logger.LogInformation("Client {ConnectionId} creating root '{Root}' in profile '{Profile}'",
        Context.ConnectionId, rootName, profileName);
    await _projectManager.CreateRootAsync(profileName, rootName, preview);
}

public async Task<RootPreview> GetRootPreview(string profileName, string rootName)
{
    _logger.LogInformation("Client {ConnectionId} getting root preview for '{Root}'",
        Context.ConnectionId, rootName);
    return await _projectManager.GetRootPreviewAsync(profileName, rootName);
}

public async Task UpdateRoot(string profileName, string rootName, RootPreview preview)
{
    _logger.LogInformation("Client {ConnectionId} updating root '{Root}'",
        Context.ConnectionId, rootName);
    await _projectManager.UpdateRootAsync(profileName, rootName, preview);
}
```

#### 8c. Add to `IProjectManager` (`src/GodMode.Server/Services/IProjectManager.cs`)

```csharp
// Root Creation
Task<RootTemplate[]> ListRootTemplatesAsync();
Task<RootPreview> PreviewRootFromTemplateAsync(string templateName, Dictionary<string, string> parameters);
Task<RootPreview> GenerateRootWithLlmAsync(RootGenerationRequest request);
Task CreateRootAsync(string profileName, string rootName, RootPreview preview);
Task<RootPreview> GetRootPreviewAsync(string profileName, string rootName);
Task UpdateRootAsync(string profileName, string rootName, RootPreview preview);
```

#### 8d. Implement in `ProjectManager`

Inject `RootTemplateService`, `RootCreator`, and `RootGenerationService` via constructor. Implement:

```csharp
public Task<RootTemplate[]> ListRootTemplatesAsync()
    => Task.FromResult(_rootTemplateService.ListTemplates());

public Task<RootPreview> PreviewRootFromTemplateAsync(string templateName, Dictionary<string, string> parameters)
    => Task.FromResult(_rootTemplateService.InstantiateTemplate(templateName, parameters));

public async Task<RootPreview> GenerateRootWithLlmAsync(RootGenerationRequest request)
    => await _rootGenerationService.GenerateAsync(request);

public Task CreateRootAsync(string profileName, string rootName, RootPreview preview)
{
    var validation = _rootCreator.Validate(preview);
    if (validation != null)
        throw new InvalidOperationException(validation);

    // Determine target path within ProjectRootsDir
    var rootsDir = _configuration["ProjectRootsDir"]
        ?? throw new InvalidOperationException("ProjectRootsDir not configured");
    var rootPath = Path.Combine(rootsDir, rootName);

    if (Directory.Exists(Path.Combine(rootPath, ".godmode-root")))
        throw new InvalidOperationException($"Root '{rootName}' already exists");

    Directory.CreateDirectory(rootPath);
    _rootCreator.WriteRoot(rootPath, preview);

    // Trigger snapshot rebuild so the new root appears immediately
    _ = RebuildSnapshotAsync();

    return Task.CompletedTask;
}

public Task<RootPreview> GetRootPreviewAsync(string profileName, string rootName)
{
    var rootPath = ResolveRootPath(profileName, rootName);
    return Task.FromResult(_rootCreator.ReadExistingRoot(rootPath));
}

public Task UpdateRootAsync(string profileName, string rootName, RootPreview preview)
{
    var validation = _rootCreator.Validate(preview);
    if (validation != null)
        throw new InvalidOperationException(validation);

    var rootPath = ResolveRootPath(profileName, rootName);
    _rootCreator.WriteRoot(rootPath, preview);

    _ = RebuildSnapshotAsync();
    return Task.CompletedTask;
}
```

### Step 9: Register New Services in DI

In `src/GodMode.Server/Program.cs`, add after existing registrations:

```csharp
builder.Services.AddSingleton<RootTemplateService>();
builder.Services.AddSingleton<RootCreator>();
builder.Services.AddSingleton<RootGenerationService>();
```

---

## Feature 2: Root Sharing (Export/Import)

### Step 10: Package Models

#### `src/GodMode.Shared/Models/RootManifest.cs` (NEW)

```csharp
namespace GodMode.Shared.Models;

/// <summary>
/// Metadata for a portable root package (.gmroot file).
/// Stored inside the package and also used for the community index.
/// </summary>
public record RootManifest(
    string Name,
    string DisplayName,
    string? Description = null,
    string? Author = null,
    string? Version = null,
    string[]? Platforms = null,
    string[]? Tags = null,
    string? Source = null,
    string? MinGodModeVersion = null
);

/// <summary>
/// Preview of a shared root before installation.
/// Includes the manifest, file listing, and script contents for security review.
/// </summary>
public record SharedRootPreview(
    RootManifest Manifest,
    Dictionary<string, string> Files,
    Dictionary<string, string>? ScriptHashes = null
);

/// <summary>
/// Tracks an installed shared root for update detection.
/// Stored in ProjectRootsDir/installed.json.
/// </summary>
public record InstalledRootInfo(
    string RootName,
    string Source,
    string? Version = null,
    string? CommitSha = null,
    DateTime InstalledAt = default,
    Dictionary<string, string>? ScriptHashes = null
);
```

### Step 11: Root Packager Service

#### `src/GodMode.Server/Services/RootPackager.cs` (NEW)

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Creates and extracts .gmroot packages (ZIP archives of .godmode-root/ contents).
/// </summary>
public class RootPackager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Exports an existing root to a .gmroot package (ZIP bytes).
    /// </summary>
    public byte[] Export(string rootPath, RootManifest? manifest = null)
    {
        var godmodeRootPath = Path.Combine(rootPath, ".godmode-root");
        if (!Directory.Exists(godmodeRootPath))
            throw new InvalidOperationException($"No .godmode-root directory found at {rootPath}");

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Generate manifest from config if not provided
            manifest ??= GenerateManifest(godmodeRootPath);
            var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            AddEntry(archive, "manifest.json", manifestJson);

            // Add all files from .godmode-root/
            foreach (var filePath in Directory.GetFiles(godmodeRootPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(godmodeRootPath, filePath);
                var content = File.ReadAllBytes(filePath);
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Extracts a .gmroot package to a SharedRootPreview for review before installation.
    /// </summary>
    public SharedRootPreview Extract(byte[] packageBytes)
    {
        using var memoryStream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        RootManifest? manifest = null;
        var files = new Dictionary<string, string>();
        var scriptHashes = new Dictionary<string, string>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // Skip directories

            using var reader = new StreamReader(entry.Open());
            var content = reader.ReadToEnd();

            if (entry.FullName == "manifest.json")
            {
                manifest = JsonSerializer.Deserialize<RootManifest>(content, JsonOptions);
            }
            else
            {
                files[entry.FullName] = content;

                // Hash script files for security verification
                if (IsScriptFile(entry.FullName))
                {
                    scriptHashes[entry.FullName] = ComputeHash(content);
                }
            }
        }

        manifest ??= new RootManifest("unknown", "Unknown Root");
        return new SharedRootPreview(manifest, files, scriptHashes);
    }

    /// <summary>
    /// Extracts a .gmroot package from a URL.
    /// </summary>
    public async Task<SharedRootPreview> ExtractFromUrlAsync(string url, HttpClient http, CancellationToken ct = default)
    {
        var bytes = await http.GetByteArrayAsync(url, ct);
        return Extract(bytes);
    }

    /// <summary>
    /// Computes SHA-256 hash of a string.
    /// </summary>
    public static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    private static bool IsScriptFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".sh" or ".ps1" or ".cmd" or ".bat";
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static RootManifest GenerateManifest(string godmodeRootPath)
    {
        var configPath = Path.Combine(godmodeRootPath, "config.json");
        var name = Path.GetFileName(Path.GetDirectoryName(godmodeRootPath)) ?? "root";
        string? description = null;

        if (File.Exists(configPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(configPath));
                if (config.TryGetProperty("description", out var desc))
                    description = desc.GetString();
            }
            catch { /* ignore parse errors */ }
        }

        // Detect which platforms have scripts
        var platforms = new List<string>();
        var hasShScripts = Directory.GetFiles(godmodeRootPath, "*.sh", SearchOption.AllDirectories).Length > 0;
        var hasPs1Scripts = Directory.GetFiles(godmodeRootPath, "*.ps1", SearchOption.AllDirectories).Length > 0;
        if (hasShScripts) { platforms.Add("macos"); platforms.Add("linux"); }
        if (hasPs1Scripts) platforms.Add("windows");

        return new RootManifest(
            Name: name,
            DisplayName: name,
            Description: description,
            Platforms: platforms.Count > 0 ? platforms.ToArray() : null
        );
    }
}
```

### Step 12: Root Installer Service

#### `src/GodMode.Server/Services/RootInstaller.cs` (NEW)

```csharp
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Installs shared roots from packages, URLs, or git repositories into ProjectRootsDir.
/// Tracks installed roots in installed.json for update detection.
/// </summary>
public class RootInstaller
{
    private readonly RootPackager _packager;
    private readonly RootCreator _creator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RootInstaller> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public RootInstaller(
        RootPackager packager,
        RootCreator creator,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RootInstaller> logger)
    {
        _packager = packager;
        _creator = creator;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Installs a root from a SharedRootPreview (already extracted/reviewed by user).
    /// </summary>
    public void Install(SharedRootPreview preview, string? localName = null)
    {
        var rootsDir = GetRootsDir();
        var name = localName ?? preview.Manifest.Name;
        var rootPath = Path.Combine(rootsDir, name);

        if (Directory.Exists(Path.Combine(rootPath, ".godmode-root")))
            throw new InvalidOperationException($"Root '{name}' already exists. Uninstall first or choose a different name.");

        // Write files
        var rootPreview = new RootPreview(preview.Files);
        var validation = _creator.Validate(rootPreview);
        if (validation != null)
            throw new InvalidOperationException($"Package validation failed: {validation}");

        Directory.CreateDirectory(rootPath);
        _creator.WriteRoot(rootPath, rootPreview);

        // Track installation
        var installed = new InstalledRootInfo(
            RootName: name,
            Source: preview.Manifest.Source ?? "file",
            Version: preview.Manifest.Version,
            InstalledAt: DateTime.UtcNow,
            ScriptHashes: preview.ScriptHashes
        );
        SaveInstalledInfo(name, installed);

        _logger.LogInformation("Installed shared root '{Name}' from {Source}", name, installed.Source);
    }

    /// <summary>
    /// Previews a root from a URL (downloads .gmroot package).
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromUrlAsync(string url, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient();
        return await _packager.ExtractFromUrlAsync(url, http, ct);
    }

    /// <summary>
    /// Previews a root from raw package bytes (file upload).
    /// </summary>
    public SharedRootPreview PreviewFromBytes(byte[] packageBytes)
    {
        return _packager.Extract(packageBytes);
    }

    /// <summary>
    /// Previews a root from a git repository.
    /// Clones (shallow) to temp dir, looks for .godmode-root/, extracts.
    /// </summary>
    public async Task<SharedRootPreview> PreviewFromGitAsync(string repoUrl, string? subPath = null, string? gitRef = null, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"godmode-git-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Shallow clone
            var refArg = gitRef != null ? $"--branch {gitRef}" : "";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --depth 1 {refArg} {repoUrl} .",
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"git clone failed: {stderr}");
            }

            // Find .godmode-root/
            var searchPath = subPath != null ? Path.Combine(tempDir, subPath) : tempDir;
            var godmodeRoot = Path.Combine(searchPath, ".godmode-root");

            if (!Directory.Exists(godmodeRoot))
                throw new InvalidOperationException($"No .godmode-root/ directory found in {(subPath ?? "repository root")}");

            // Read files
            var preview = _creator.ReadExistingRoot(searchPath);

            // Get commit SHA
            string? commitSha = null;
            try
            {
                var shaProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse HEAD",
                        WorkingDirectory = tempDir,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                shaProcess.Start();
                commitSha = (await shaProcess.StandardOutput.ReadToEndAsync(ct)).Trim();
                await shaProcess.WaitForExitAsync(ct);
            }
            catch { /* ignore */ }

            // Build manifest
            var manifest = new RootManifest(
                Name: Path.GetFileName(searchPath),
                DisplayName: Path.GetFileName(searchPath),
                Source: repoUrl + (subPath != null ? $"/{subPath}" : "") + (gitRef != null ? $"@{gitRef}" : "")
            );

            // Compute script hashes
            var scriptHashes = new Dictionary<string, string>();
            foreach (var (path, content) in preview.Files)
            {
                if (path.EndsWith(".sh") || path.EndsWith(".ps1") || path.EndsWith(".cmd") || path.EndsWith(".bat"))
                    scriptHashes[path] = RootPackager.ComputeHash(content);
            }

            return new SharedRootPreview(manifest, preview.Files, scriptHashes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Removes an installed shared root.
    /// </summary>
    public void Uninstall(string rootName)
    {
        var rootsDir = GetRootsDir();
        var rootPath = Path.Combine(rootsDir, rootName);

        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);

        RemoveInstalledInfo(rootName);
        _logger.LogInformation("Uninstalled shared root '{Name}'", rootName);
    }

    /// <summary>
    /// Gets all installed root tracking info.
    /// </summary>
    public Dictionary<string, InstalledRootInfo> GetInstalledRoots()
    {
        var path = GetInstalledJsonPath();
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, InstalledRootInfo>>(json, JsonOptions) ?? new();
    }

    private void SaveInstalledInfo(string name, InstalledRootInfo info)
    {
        var installed = GetInstalledRoots();
        installed[name] = info;
        var path = GetInstalledJsonPath();
        File.WriteAllText(path, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private void RemoveInstalledInfo(string name)
    {
        var installed = GetInstalledRoots();
        installed.Remove(name);
        var path = GetInstalledJsonPath();
        File.WriteAllText(path, JsonSerializer.Serialize(installed, JsonOptions));
    }

    private string GetRootsDir() =>
        _configuration["ProjectRootsDir"]
        ?? throw new InvalidOperationException("ProjectRootsDir not configured");

    private string GetInstalledJsonPath() =>
        Path.Combine(GetRootsDir(), "installed.json");
}
```

### Step 13: SignalR Hub Methods for Sharing

#### 13a. Add to `IProjectHub`

```csharp
// Root Sharing
Task<byte[]> ExportRoot(string profileName, string rootName);
Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes);
Task<SharedRootPreview> PreviewImportFromUrl(string url);
Task<SharedRootPreview> PreviewImportFromGit(string repoUrl, string? subPath, string? gitRef);
Task InstallSharedRoot(SharedRootPreview preview, string? localName);
Task UninstallSharedRoot(string rootName);
```

#### 13b. Implement in `ProjectHub`

```csharp
public async Task<byte[]> ExportRoot(string profileName, string rootName)
{
    _logger.LogInformation("Client {ConnectionId} exporting root '{Root}'",
        Context.ConnectionId, rootName);
    return await _projectManager.ExportRootAsync(profileName, rootName);
}

public async Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes)
{
    _logger.LogInformation("Client {ConnectionId} previewing root import from file",
        Context.ConnectionId);
    return await _projectManager.PreviewImportFromBytesAsync(packageBytes);
}

public async Task<SharedRootPreview> PreviewImportFromUrl(string url)
{
    _logger.LogInformation("Client {ConnectionId} previewing root import from URL: {Url}",
        Context.ConnectionId, url);
    return await _projectManager.PreviewImportFromUrlAsync(url);
}

public async Task<SharedRootPreview> PreviewImportFromGit(string repoUrl, string? subPath, string? gitRef)
{
    _logger.LogInformation("Client {ConnectionId} previewing root import from git: {Url}",
        Context.ConnectionId, repoUrl);
    return await _projectManager.PreviewImportFromGitAsync(repoUrl, subPath, gitRef);
}

public async Task InstallSharedRoot(SharedRootPreview preview, string? localName)
{
    _logger.LogInformation("Client {ConnectionId} installing shared root '{Name}'",
        Context.ConnectionId, preview.Manifest.Name);
    await _projectManager.InstallSharedRootAsync(preview, localName);
}

public async Task UninstallSharedRoot(string rootName)
{
    _logger.LogInformation("Client {ConnectionId} uninstalling root '{Name}'",
        Context.ConnectionId, rootName);
    await _projectManager.UninstallSharedRootAsync(rootName);
}
```

#### 13c. Add to `IProjectManager` and implement in `ProjectManager`

Add signatures matching the hub methods. Implement by delegating to `RootPackager`, `RootInstaller`, and `RootCreator`. After install/uninstall, call `RebuildSnapshotAsync()`.

### Step 14: Register Sharing Services in DI

In `src/GodMode.Server/Program.cs`:

```csharp
builder.Services.AddSingleton<RootPackager>();
builder.Services.AddSingleton<RootInstaller>();
```

Also ensure `IHttpClientFactory` is available (it is, because `AddHttpClient<McpRegistryClient>()` registers the factory).

### Step 15: Client-Side Service Methods

#### Add to `IProjectService` (`src/GodMode.ClientBase/Services/IProjectService.cs`)

```csharp
// Root Creation
Task<RootTemplate[]> ListRootTemplatesAsync(string profileName, string hostId);
Task<RootPreview> PreviewRootFromTemplateAsync(string profileName, string hostId, string templateName, Dictionary<string, string> parameters);
Task<RootPreview> GenerateRootWithLlmAsync(string profileName, string hostId, RootGenerationRequest request);
Task CreateRootAsync(string profileName, string hostId, string rootName, RootPreview preview);
Task<RootPreview> GetRootPreviewAsync(string profileName, string hostId, string profileN, string rootName);
Task UpdateRootAsync(string profileName, string hostId, string profileN, string rootName, RootPreview preview);

// Root Sharing
Task<byte[]> ExportRootAsync(string profileName, string hostId, string rootName);
Task<SharedRootPreview> PreviewImportFromBytesAsync(string profileName, string hostId, byte[] packageBytes);
Task<SharedRootPreview> PreviewImportFromUrlAsync(string profileName, string hostId, string url);
Task<SharedRootPreview> PreviewImportFromGitAsync(string profileName, string hostId, string repoUrl, string? subPath, string? gitRef);
Task InstallSharedRootAsync(string profileName, string hostId, SharedRootPreview preview, string? localName);
Task UninstallSharedRootAsync(string profileName, string hostId, string rootName);
```

#### Implement in `SignalRProjectConnection`

Follow existing pattern — each method calls the hub connection method.

---

## File Manifest

### New Files

| File | Description |
|------|-------------|
| `src/GodMode.Shared/Models/RootTemplate.cs` | Template model + parameters |
| `src/GodMode.Shared/Models/RootPreview.cs` | File preview before writing |
| `src/GodMode.Shared/Models/RootGenerationRequest.cs` | LLM generation request + schema field definitions |
| `src/GodMode.Shared/Models/RootManifest.cs` | Package metadata + installed root tracking |
| `src/GodMode.Server/Services/RootTemplateService.cs` | Template discovery + instantiation |
| `src/GodMode.Server/Services/RootCreator.cs` | Writes/reads/validates .godmode-root/ |
| `src/GodMode.Server/Services/RootGenerationService.cs` | LLM-assisted generation via InferenceRouter |
| `src/GodMode.Server/Services/RootPackager.cs` | .gmroot ZIP export/extract |
| `src/GodMode.Server/Services/RootInstaller.cs` | Install from package/URL/git |
| `src/GodMode.Server/Templates/` | Bundled template resources (5 templates) |
| `src/GodMode.Avalonia/ViewModels/SchemaEditorViewModel.cs` | Visual schema editor logic |
| `src/GodMode.Avalonia/ViewModels/CreateRootViewModel.cs` | Root creation flow |
| `src/GodMode.Avalonia/Views/SchemaEditorView.axaml` | Schema editor UI |
| `src/GodMode.Avalonia/Views/SchemaEditorView.axaml.cs` | Schema editor code-behind |
| `src/GodMode.Avalonia/Views/CreateRootView.axaml` | Root creation UI |
| `src/GodMode.Avalonia/Views/CreateRootView.axaml.cs` | Root creation code-behind |

### Modified Files

| File | Change |
|------|--------|
| `src/GodMode.Shared/Hubs/IProjectHub.cs` | Add 12 new hub methods (6 creation + 6 sharing) |
| `src/GodMode.Server/Hubs/ProjectHub.cs` | Implement 12 new hub methods |
| `src/GodMode.Server/Services/IProjectManager.cs` | Add 12 new method signatures |
| `src/GodMode.Server/Services/ProjectManager.cs` | Implement 12 new methods, inject new services |
| `src/GodMode.Server/Program.cs` | Register 5 new services, add GodMode.AI reference |
| `src/GodMode.Server/GodMode.Server.csproj` | Add GodMode.AI project reference, embed templates |
| `src/GodMode.ClientBase/Services/IProjectService.cs` | Add 12 client-side method signatures |
| `src/GodMode.ClientBase/Services/SignalRProjectConnection.cs` | Implement 12 client-side methods |

---

## Real Examples for Reference

### Existing root: godmode-dev

Located at `.devcontainer/godmode-server/roots/godmode-dev/.godmode-root/`. This is the canonical example of a multi-action root with:
- Base config (`config.json`): description, profileName, environment, claudeArgs, prepare/delete scripts
- Three action overlays: `config.freeform.json`, `config.issue.json`, `config.explore.json`
- Per-action schemas: `freeform/schema.json`, `issue/schema.json`, `explore/schema.json`
- Cross-platform scripts: create, prepare, delete in both .sh and .ps1
- Templates: `nameTemplate: "issue_{issueNumber}"`, `promptTemplate: "Read GitHub issue #{issueNumber}..."`
- Script result override via `$GODMODE_RESULT_FILE`

Use this as the basis for the `git-worktree` template (replacing hardcoded repo URLs with `{{repoUrl}}`).

### Existing root: ad-hoc

Located at `.devcontainer/godmode-server/roots/ad-hoc/.godmode-root/`. Minimal single-action root:
```json
{ "description": "Ad-hoc tasks — just a folder with Claude, no VCS", "model": "sonnet" }
```

Use this as the basis for the `ad-hoc` and `blank` templates.

### Script environment variables reference

Scripts receive (from `ProjectManager.BuildScriptEnvironment`):
- `GODMODE_ROOT_PATH`, `GODMODE_PROJECT_PATH`, `GODMODE_PROJECT_ID`, `GODMODE_PROJECT_NAME`
- `GODMODE_RESULT_FILE` — scripts can write `project_path=...` and `project_name=...` to override
- `GODMODE_INPUT_{KEY}` — form fields (camelCase → UPPER_SNAKE_CASE)
- `GODMODE_FORCE` — "true" when force-deleting

---

## Verification

### Build
```bash
dotnet build
```
Must compile cleanly.

### Unit Tests

Create `tests/GodMode.Server.Tests/RootTemplateServiceTests.cs`:
- Test ListTemplates returns non-empty array
- Test InstantiateTemplate replaces placeholders correctly
- Test InstantiateTemplate with missing required parameter

Create `tests/GodMode.Server.Tests/RootCreatorTests.cs`:
- Test Validate accepts valid config.json
- Test Validate rejects missing config.json
- Test Validate rejects invalid JSON
- Test WriteRoot creates correct directory structure
- Test ReadExistingRoot round-trips with WriteRoot

Create `tests/GodMode.Server.Tests/RootPackagerTests.cs`:
- Test Export/Extract round-trip preserves all files
- Test ComputeHash returns consistent results
- Test Extract generates manifest from config when no manifest.json present

Create `tests/GodMode.Server.Tests/SchemaEditorViewModelTests.cs`:
- Test SerializeToJsonSchema produces valid JSON Schema
- Test LoadFromJsonSchema round-trips with SerializeToJsonSchema
- Test AddField/RemoveField updates collection

### Manual E2E

1. Start server: `dotnet run --project src/GodMode.Server/GodMode.Server.csproj`
2. Start desktop app: `dotnet run --project src/GodMode.Avalonia.Desktop/GodMode.Avalonia.Desktop.csproj`
3. Click "New Root" → select "Git Worktree" template → fill in repo URL → schema editor appears with default fields
4. Add a custom field in schema editor → verify preview updates
5. Click "Generate Script" → describe what the script should do → verify LLM generates .sh and .ps1
6. Preview all files → Save → verify root appears in project creation dropdown
7. Create a project from the new root → verify it works end-to-end
8. Right-click root → "Export" → save .gmroot file
9. "Import" → select the .gmroot file → review scripts → install with different name → verify it appears
10. "Import from URL" → paste URL to a .gmroot file → verify preview and install
11. "Import from Git" → paste repo URL → verify .godmode-root/ extraction and install
