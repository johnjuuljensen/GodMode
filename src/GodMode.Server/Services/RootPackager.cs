using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Exports project roots as .gmroot ZIP packages with embedded manifest and script hashes.
/// </summary>
public class RootPackager
{
    private readonly RootCreator _rootCreator;
    private readonly ILogger<RootPackager> _logger;

    public RootPackager(RootCreator rootCreator, ILogger<RootPackager> logger)
    {
        _rootCreator = rootCreator;
        _logger = logger;
    }

    /// <summary>
    /// Exports a root directory as a .gmroot ZIP package (byte array).
    /// </summary>
    public byte[] Export(string rootPath, string rootName, string? description = null)
    {
        var preview = _rootCreator.ReadExistingRoot(rootPath)
            ?? throw new InvalidOperationException($"No .godmode-root/ found at '{rootPath}'.");

        // Build script hashes for integrity verification
        var scriptHashes = new Dictionary<string, string>();
        foreach (var (path, content) in preview.Files)
        {
            if (IsScript(path))
                scriptHashes[path] = ComputeSha256(content);
        }

        var manifest = new RootManifest(
            Name: rootName,
            Description: description,
            ExportedAt: DateTime.UtcNow,
            ScriptHashes: scriptHashes.Count > 0 ? scriptHashes : null);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write manifest
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            WriteEntry(archive, "manifest.json", manifestJson);

            // Write all root files (exclude source.json — it's installation metadata, not portable content)
            foreach (var (path, content) in preview.Files)
            {
                if (path.Equals("source.json", StringComparison.OrdinalIgnoreCase)) continue;
                WriteEntry(archive, $".godmode-root/{path}", content);
            }
        }

        ms.Position = 0;
        _logger.LogInformation("Exported root '{RootName}' ({FileCount} files, {Size} bytes)",
            rootName, preview.Files.Count, ms.Length);

        return ms.ToArray();
    }

    /// <summary>
    /// Reads a .gmroot package and returns a SharedRootPreview for review before installation.
    /// </summary>
    public static SharedRootPreview PreviewFromBytes(byte[] packageBytes, string source = "bytes")
    {
        using var ms = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        // Read manifest
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Package missing manifest.json.");
        var manifest = JsonSerializer.Deserialize<RootManifest>(
            new StreamReader(manifestEntry.Open()).ReadToEnd())
            ?? throw new InvalidOperationException("Invalid manifest.json.");

        // Read root files
        var files = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(".godmode-root/")) continue;
            var relativePath = entry.FullName[".godmode-root/".Length..];
            if (string.IsNullOrEmpty(relativePath)) continue;
            using var reader = new StreamReader(entry.Open());
            files[relativePath] = reader.ReadToEnd();
        }

        return new SharedRootPreview(manifest, new RootPreview(files), source);
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static bool IsScript(string path) =>
        path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
