using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Validates project folder structure and integrity.
/// </summary>
public static class ProjectValidator
{
    /// <summary>
    /// Validation result containing errors and warnings.
    /// </summary>
    public record ValidationResult
    {
        /// <summary>
        /// Gets whether the project is valid.
        /// </summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>
        /// Gets the list of validation errors.
        /// </summary>
        public List<string> Errors { get; init; } = new();

        /// <summary>
        /// Gets the list of validation warnings.
        /// </summary>
        public List<string> Warnings { get; init; } = new();

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        public static ValidationResult Success() => new();

        /// <summary>
        /// Adds an error to the result.
        /// </summary>
        public ValidationResult WithError(string error)
        {
            Errors.Add(error);
            return this;
        }

        /// <summary>
        /// Adds a warning to the result.
        /// </summary>
        public ValidationResult WithWarning(string warning)
        {
            Warnings.Add(warning);
            return this;
        }
    }

    /// <summary>
    /// Validates a project folder structure and contents.
    /// </summary>
    /// <param name="projectPath">Path to the project folder.</param>
    /// <returns>Validation result with errors and warnings.</returns>
    public static ValidationResult ValidateProject(string projectPath)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return result.WithError("Project path is null or empty");
        }

        if (!Directory.Exists(projectPath))
        {
            return result.WithError($"Project directory does not exist: {projectPath}");
        }

        // Check required files
        var statusPath = Path.Combine(projectPath, "status.json");
        var inputPath = Path.Combine(projectPath, "input.jsonl");
        var outputPath = Path.Combine(projectPath, "output.jsonl");
        var workPath = Path.Combine(projectPath, "work");

        if (!File.Exists(statusPath))
        {
            result.WithError("Missing required file: status.json");
        }
        else
        {
            // Validate status.json format
            try
            {
                var json = File.ReadAllText(statusPath);
                var status = JsonSerializer.Deserialize<ProjectStatus>(json, ProjectJsonContext.Default.ProjectStatus);
                if (status == null)
                {
                    result.WithError("status.json is null or invalid");
                }
                else
                {
                    // Validate status contents
                    if (string.IsNullOrWhiteSpace(status.Id))
                        result.WithError("status.json: Id is null or empty");

                    if (string.IsNullOrWhiteSpace(status.Name))
                        result.WithError("status.json: Name is null or empty");

                    if (status.CreatedAt > DateTime.UtcNow)
                        result.WithWarning("status.json: CreatedAt is in the future");

                    if (status.UpdatedAt < status.CreatedAt)
                        result.WithError("status.json: UpdatedAt is before CreatedAt");

                    if (status.OutputOffset < 0)
                        result.WithError("status.json: OutputOffset is negative");
                }
            }
            catch (JsonException ex)
            {
                result.WithError($"status.json is not valid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                result.WithError($"Error reading status.json: {ex.Message}");
            }
        }

        if (!File.Exists(inputPath))
        {
            result.WithWarning("Missing input.jsonl file");
        }
        else
        {
            // Validate input.jsonl format
            ValidateJsonlFile(inputPath, "input.jsonl", result);
        }

        if (!File.Exists(outputPath))
        {
            result.WithWarning("Missing output.jsonl file");
        }
        else
        {
            // Validate output.jsonl format
            ValidateJsonlFile(outputPath, "output.jsonl", result);
        }

        if (!Directory.Exists(workPath))
        {
            result.WithWarning("Missing work directory");
        }

        return result;
    }

    /// <summary>
    /// Validates a JSONL file format.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <param name="fileName">Friendly name for error messages.</param>
    /// <param name="result">Validation result to append errors to.</param>
    private static void ValidateJsonlFile(string filePath, string fileName, ValidationResult result)
    {
        try
        {
            var lineNumber = 0;
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var evt = JsonSerializer.Deserialize<OutputEvent>(line, ProjectJsonContext.Default.OutputEvent);
                    if (evt == null)
                    {
                        result.WithWarning($"{fileName} line {lineNumber}: Failed to parse event");
                    }
                }
                catch (JsonException ex)
                {
                    result.WithWarning($"{fileName} line {lineNumber}: Invalid JSON - {ex.Message}");
                }
            }

            // Don't report warnings for empty files
            if (lineNumber == 0)
            {
                result.WithWarning($"{fileName} is empty");
            }
        }
        catch (Exception ex)
        {
            result.WithError($"Error reading {fileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to repair common project folder issues.
    /// </summary>
    /// <param name="projectPath">Path to the project folder.</param>
    /// <returns>True if repairs were successful.</returns>
    public static bool TryRepairProject(string projectPath)
    {
        if (!Directory.Exists(projectPath))
            return false;

        try
        {
            var statusPath = Path.Combine(projectPath, "status.json");
            var inputPath = Path.Combine(projectPath, "input.jsonl");
            var outputPath = Path.Combine(projectPath, "output.jsonl");
            var workPath = Path.Combine(projectPath, "work");

            // Create missing files with safe defaults
            if (!File.Exists(inputPath))
            {
                File.WriteAllText(inputPath, string.Empty);
            }

            if (!File.Exists(outputPath))
            {
                File.WriteAllText(outputPath, string.Empty);
            }

            if (!Directory.Exists(workPath))
            {
                Directory.CreateDirectory(workPath);
            }

            // Can't repair missing status.json - too critical
            if (!File.Exists(statusPath))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a project path looks valid (directory exists with status.json).
    /// </summary>
    /// <param name="projectPath">Path to check.</param>
    /// <returns>True if path appears to be a valid project folder.</returns>
    public static bool IsValidProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        if (!Directory.Exists(projectPath))
            return false;

        var statusPath = Path.Combine(projectPath, "status.json");
        return File.Exists(statusPath);
    }

    /// <summary>
    /// Gets the project ID from a project path.
    /// </summary>
    /// <param name="projectPath">Path to the project folder.</param>
    /// <returns>Project ID (folder name), or null if invalid.</returns>
    public static string? GetProjectIdFromPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return null;

        try
        {
            return Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return null;
        }
    }
}
