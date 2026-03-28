using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads bundled root templates from the Templates/ directory.
/// Instantiates templates by replacing {{param}} placeholders with user values.
/// </summary>
public class RootTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _templatesDir;
    private readonly ILogger<RootTemplateService> _logger;

    public RootTemplateService(ILogger<RootTemplateService> logger)
    {
        _logger = logger;
        // Templates are copied to output directory via Content items in csproj
        _templatesDir = Path.Combine(AppContext.BaseDirectory, "Templates");
    }

    /// <summary>
    /// Lists all available templates.
    /// </summary>
    public RootTemplate[] ListTemplates()
    {
        if (!Directory.Exists(_templatesDir))
        {
            _logger.LogWarning("Templates directory not found at {Path}", _templatesDir);
            return [];
        }

        var templates = new List<RootTemplate>();
        foreach (var templateDir in Directory.GetDirectories(_templatesDir))
        {
            var manifestPath = Path.Combine(templateDir, "template.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var template = JsonSerializer.Deserialize<RootTemplate>(json, JsonOptions);
                if (template != null)
                    templates.Add(template);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load template from {Path}", manifestPath);
            }
        }

        return templates.ToArray();
    }

    /// <summary>
    /// Instantiates a template with user-provided parameter values.
    /// Returns a RootPreview with all files ready to write.
    /// </summary>
    public RootPreview InstantiateTemplate(string templateName, Dictionary<string, string> parameters)
    {
        var templateDir = Path.Combine(_templatesDir, templateName);
        if (!Directory.Exists(templateDir))
            return new RootPreview(new Dictionary<string, string>(), $"Template '{templateName}' not found");

        var files = new Dictionary<string, string>();

        foreach (var filePath in Directory.GetFiles(templateDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(templateDir, filePath);

            // Skip template.json manifest — it's metadata, not a root file
            if (relativePath == "template.json") continue;

            var content = File.ReadAllText(filePath);

            // Replace {{param}} placeholders
            foreach (var (key, value) in parameters)
                content = content.Replace($"{{{{{key}}}}}", value);

            files[relativePath] = content;
        }

        return new RootPreview(files);
    }
}
