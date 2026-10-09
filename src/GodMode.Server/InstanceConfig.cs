using GodMode.Server.Services;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace GodMode.Server;

/// <summary>
/// The config file of one server instance, named when it starts: <c>--config &lt;path&gt;</c>, else the
/// <c>GODMODE_CONFIG</c> environment variable. It is a JSON config source after <c>appsettings.json</c>
/// and <c>appsettings.{Environment}.json</c>, and before environment variables and the command line,
/// reloaded when it changes. There is no default one: a server started without it runs on appsettings.
/// It may hold the key and the profiles' secrets, so it is never where sessions work: under a root
/// source, or the fallback root (<see cref="ServerFiles"/>).
/// </summary>
public static class InstanceConfig
{
    public const string CommandLineSetting = "config";
    public const string EnvironmentVariable = "GODMODE_CONFIG";

    /// <summary>The full path of the instance's config file <paramref name="settings"/> name; null when they name none.</summary>
    public static string? PathFrom(IConfiguration settings) =>
        (settings[CommandLineSetting] is { Length: > 0 } named ? named : settings[EnvironmentVariable]) is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : null;

    /// <summary>
    /// Adds the instance's config file to <paramref name="configuration"/>, in its place among the
    /// sources, and returns its full path; null when none is named. Throws
    /// <see cref="StartupConfigurationException"/> when the named file does not exist, or is under a
    /// root source or the fallback root, as the config names them with the file in.
    /// </summary>
    public static string? AddTo(IConfigurationBuilder configuration, IConfiguration settings, string environmentName)
    {
        if (PathFrom(settings) is not { } fullPath)
            return null;

        if (!File.Exists(fullPath))
            throw new StartupConfigurationException(
                $"GodMode.Server will not start: its config file, {fullPath} (--{CommandLineSetting} or {EnvironmentVariable}), does not exist.");

        var source = new LastGoodJsonSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(fullPath)!),
            Path = Path.GetFileName(fullPath),
            // Named and there at the start; it may go away later, and the server keeps running on the rest
            Optional = true,
            ReloadOnChange = true,
        };
        var sources = configuration.Sources;
        sources.Insert(AfterAppSettings(sources, environmentName), source);

        if (ServerFiles.FolderHolding(fullPath, settings) is { } holder)
            throw new StartupConfigurationException(
                $"GodMode.Server will not start: its config file, {fullPath}, is under {holder.Setting} ({holder.Folder}), " +
                $"where sessions work, and it may hold the key and the profiles' secrets. Keep it in a folder of its own, outside every root.");
        return fullPath;
    }

    /// <summary>
    /// Has <paramref name="report"/> told of every reload of the instance's file in <paramref name="configuration"/>
    /// that is refused, with the file's path and why: until it is called, that goes to the console's error.
    /// </summary>
    public static void ReportRefusedReloads(IConfigurationBuilder configuration, Action<string, Exception> report)
    {
        foreach (var source in configuration.Sources.OfType<LastGoodJsonSource>())
            source.ReloadRefused = report;
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

    /// <summary>
    /// The instance's file as a JSON source whose reload keeps the config it last read when the file does not
    /// parse (a save half done, a typo), rather than none of it, and says why. Its first read still fails on one:
    /// a start on a broken file stops. A file that goes away is read as empty, as any optional file is.
    /// </summary>
    private sealed class LastGoodJsonSource : JsonConfigurationSource
    {
        public Action<string, Exception> ReloadRefused { get; set; } =
            (path, error) => Console.Error.WriteLine($"The config file {path} was not reloaded, and its last good config is kept: {error.Message}");

        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new Provider(this);
        }

        private sealed class Provider(LastGoodJsonSource source) : JsonConfigurationProvider(source)
        {
            private bool _loaded;

            public override void Load(Stream stream)
            {
                try
                {
                    base.Load(stream);
                    _loaded = true;
                }
                catch (Exception ex) when (_loaded)
                {
                    // The data stays what the last good read made it
                    source.ReloadRefused(source.FileProvider?.GetFileInfo(source.Path ?? "").PhysicalPath ?? source.Path ?? "", ex);
                }
            }
        }
    }
}
