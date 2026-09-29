using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Layout;

/// <summary>A window's place on the virtual screen, in physical pixels.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// One open window as the app last saw it (#341): its profile (null for the main window), its bounds when not
/// maximised, whether it was maximised, and its virtual desktop (null when it wasn't known).
/// </summary>
public sealed record SavedWindow(string? Profile, WindowBounds Bounds, bool Maximized, Guid? Desktop);

/// <summary>The app's open windows, main window first, as <c>windows.json</c> in the app's data folder holds them.</summary>
public sealed record WindowLayout(IReadOnlyList<SavedWindow> Windows)
{
    public const string FileName = "windows.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The main window's entry, the first with no profile.</summary>
    public SavedWindow? Main => Windows.FirstOrDefault(w => w.Profile is null);

    /// <summary>The profile windows' entries, once per profile (by name without case), in the order they were saved.</summary>
    public IReadOnlyList<SavedWindow> Profiles =>
        [.. Windows.Where(w => !string.IsNullOrWhiteSpace(w.Profile)).DistinctBy(w => w.Profile!, StringComparer.OrdinalIgnoreCase)];

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The layout in <paramref name="json"/>, or null when it isn't one.</summary>
    public static WindowLayout? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WindowLayout>(json, Options) is { Windows: { } windows }
                ? new WindowLayout([.. windows.Where(w => w?.Bounds is not null)])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The layout the file holds, or null when there is none or it can't be read: then the app opens its main window as
    /// it would without one. windows.json is app data with no earlier shape, so nothing is migrated.
    /// </summary>
    public static WindowLayout? Load(string path, ILogger logger)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Windows: could not read {Path}: {Error}", path, ex.Message);
            return null;
        }
        var layout = FromJson(json);
        if (layout is null) logger.LogWarning("Windows: {Path} is not a window layout; opening the main window only", path);
        return layout;
    }

    /// <summary>Writes the layout to the file, whole: a partial write never replaces the last good one.</summary>
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }
}
