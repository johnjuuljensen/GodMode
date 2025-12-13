using System.Text;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Utilities for reading JSONL (JSON Lines) files incrementally.
/// </summary>
public static class JsonlReader
{
    /// <summary>
    /// Reads all output events from a JSONL file.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <returns>Enumerable of output events.</returns>
    /// <exception cref="FileNotFoundException">Thrown when file doesn't exist.</exception>
    /// <exception cref="JsonException">Thrown when JSON parsing fails.</exception>
    public static IEnumerable<OutputEvent> ReadAll(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"JSONL file not found: {filePath}");

        return ReadAllInternal(filePath);
    }

    private static IEnumerable<OutputEvent> ReadAllInternal(string filePath)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fileStream, Encoding.UTF8);

        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            OutputEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<OutputEvent>(line, ProjectJsonContext.Default.OutputEvent);
            }
            catch (JsonException ex)
            {
                throw new JsonException($"Failed to parse line {lineNumber} in {filePath}: {ex.Message}", ex);
            }

            if (evt != null)
                yield return evt;
        }
    }

    /// <summary>
    /// Reads output events from a JSONL file starting at a specific byte offset.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <param name="offset">Byte offset to start reading from.</param>
    /// <returns>Enumerable of output events and the new offset.</returns>
    /// <exception cref="FileNotFoundException">Thrown when file doesn't exist.</exception>
    /// <exception cref="JsonException">Thrown when JSON parsing fails.</exception>
    public static (IEnumerable<OutputEvent> Events, long NewOffset) ReadFrom(string filePath, long offset)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"JSONL file not found: {filePath}");

        var events = new List<OutputEvent>();
        long newOffset = offset;

        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (offset >= fileStream.Length)
                return (events, offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fileStream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    newOffset = fileStream.Position;
                    continue;
                }

                var evt = JsonSerializer.Deserialize<OutputEvent>(line, ProjectJsonContext.Default.OutputEvent);
                if (evt != null)
                    events.Add(evt);

                newOffset = fileStream.Position;
            }
        }

        return (events, newOffset);
    }

    /// <summary>
    /// Counts the number of lines in a JSONL file.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <returns>Number of non-empty lines.</returns>
    public static int CountLines(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        int count = 0;
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fileStream, Encoding.UTF8);

        while (reader.ReadLine() is string line && !string.IsNullOrWhiteSpace(line))
            count++;

        return count;
    }

    /// <summary>
    /// Gets the current file size (useful for tracking offset).
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <returns>File size in bytes, or 0 if file doesn't exist.</returns>
    public static long GetFileSize(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        return new FileInfo(filePath).Length;
    }
}
