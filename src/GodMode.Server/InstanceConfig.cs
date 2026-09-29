using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace GodMode.Server;

/// <summary>
/// The config file of one server instance, named when it starts: <c>--config &lt;path&gt;</c>, else the
/// <c>GODMODE_CONFIG</c> environment variable. It is a JSON config source after <c>appsettings.json</c>
/// and <c>appsettings.{Environment}.json</c>, and before environment variables and the command line,
/// reloaded when it changes. There is no default one: a server started without it runs on appsettings.
/// </summary>
public static class InstanceConfig
{
    public const string CommandLineSetting = "config";
    public const string EnvironmentVariable = "GODMODE_CONFIG";

    /// <summary>
    /// Adds the instance's config file to <paramref name="configuration"/>, in its place among the
    /// sources, and returns its full path; null when none is named. Throws
    /// <see cref="StartupConfigurationException"/> when the named file does not exist.
    /// </summary>
    public static string? AddTo(IConfigurationBuilder configuration, IConfiguration settings, string environmentName)
    {
        if ((settings[CommandLineSetting] is { Length: > 0 } named ? named : settings[EnvironmentVariable]) is not { Length: > 0 } path)
            return null;

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new StartupConfigurationException(
                $"GodMode.Server will not start: its config file, {fullPath} (--{CommandLineSetting} or {EnvironmentVariable}), does not exist.");

        var source = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(fullPath)!),
            Path = Path.GetFileName(fullPath),
            // Named and there at the start; it may go away later, and the server keeps running on the rest
            Optional = true,
            ReloadOnChange = true,
        };
        var sources = configuration.Sources;
        sources.Insert(AfterAppSettings(sources, environmentName), source);
        return fullPath;
    }

    /// <summary>Where the instance's file goes: right after the last of the appsettings files, else first.</summary>
    private static int AfterAppSettings(IList<IConfigurationSource> sources, string environmentName)
    {
        string[] appSettings = ["appsettings.json", $"appsettings.{environmentName}.json"];
        for (var i = sources.Count - 1; i >= 0; i--)
            if (sources[i] is JsonConfigurationSource { Path: { } file } && appSettings.Contains(file, StringComparer.OrdinalIgnoreCase))
                return i + 1;
        return 0;
    }
}
