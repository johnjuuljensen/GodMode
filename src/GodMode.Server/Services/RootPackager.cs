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
            throw new InvalidOperationException($"Root '{Path.GetFileName(rootPath)}' has no .godmode-root configuration to export");

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            manifest ??= GenerateManifest(godmodeRootPath);
            var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            AddEntry(archive, "manifest.json", manifestJson);

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
            if (string.IsNullOrEmpty(entry.Name)) continue;

            using var reader = new StreamReader(entry.Open());
            var content = reader.ReadToEnd();

            if (entry.FullName == "manifest.json")
            {
                manifest = JsonSerializer.Deserialize<RootManifest>(content, JsonOptions);
            }
            else
            {
                files[entry.FullName] = content;

                if (IsScriptFile(entry.FullName))
                    scriptHashes[entry.FullName] = ComputeHash(content);
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
