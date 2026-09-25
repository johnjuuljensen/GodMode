using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// The executables the server starts come from its configuration when that names them, not from
/// whatever is first on its <c>PATH</c>: in the Docker image the first entry is a directory the
/// session can write. A fake <c>pwsh</c> (a copy of the fake claude, which records every start in
/// the project's folder) is planted first on the server's <c>PATH</c>, and a status script is run.
/// </summary>
[Collection(ServerPathCollection.Name)]
public class ExecutableResolutionTests
{
    [Fact]
    public async Task PwshPlantedFirstOnPath_IsNotRun_WhenTheServerNamesItsPwsh()
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
            ?? throw new InvalidOperationException("the tests need pwsh on PATH");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitResult("Done."),
            rootConfig: new Dictionary<string, object> { ["status"] = "scripts/status" },
            settings: new Dictionary<string, string?> { [ScriptRunner.PowerShellExecutableSetting] = pwsh });
        var marker = Path.Combine(harness.WorkDir, "status-ran.txt");
        var scripts = Path.Combine(harness.RootPath, ".godmode-root", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "status.ps1"), $"""
            Set-Content -Path '{marker}' -Value 'ran'
            '{"{}"}'
            """);
        using var planted = new PlantedPwsh(Path.Combine(harness.WorkDir, "planted"));

        var created = await harness.CreateProjectAsync();

        bool PlantedRan() => harness.Launches(created.Id).Any(l => l.Argv.Contains("-File"));
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(File.Exists(marker) || PlantedRan()), null,
            () => $"the status script did not run.\n{harness.Describe(created.Id)}");
        Assert.False(PlantedRan(), $"the server ran the pwsh planted first on its PATH.\n{harness.Describe(created.Id)}");
        Assert.True(File.Exists(marker));
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// The fake claude, copied as <c>pwsh</c> with the files its apphost loads, into a directory put
    /// first on the test process's <c>PATH</c> (the server's) until disposed.
    /// </summary>
    private sealed class PlantedPwsh : IDisposable
    {
        private const string Fake = "GodMode.FakeClaude";
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");

        public PlantedPwsh(string directory)
        {
            Directory.CreateDirectory(directory);
            var from = AppContext.BaseDirectory;
            var apphost = OperatingSystem.IsWindows() ? ".exe" : "";
            File.Copy(Path.Combine(from, Fake + apphost), Path.Combine(directory, "pwsh" + apphost));
            foreach (var file in new[] { $"{Fake}.dll", $"{Fake}.runtimeconfig.json", $"{Fake}.deps.json" }.Concat(Dependencies(from)))
                if (File.Exists(Path.Combine(from, file)) && !File.Exists(Path.Combine(directory, file)))
                    File.Copy(Path.Combine(from, file), Path.Combine(directory, file));
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + _previousPath);
        }

        /// <summary>The assemblies the fake's deps.json names, by file name.</summary>
        private static IEnumerable<string> Dependencies(string directory)
        {
            using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, $"{Fake}.deps.json")));
            return deps.RootElement.GetProperty("targets").EnumerateObject()
                .SelectMany(target => target.Value.EnumerateObject())
                .SelectMany(library => library.Value.TryGetProperty("runtime", out var runtime) ? runtime.EnumerateObject() : [])
                .Select(assembly => Path.GetFileName(assembly.Name))
                .ToList();
        }

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _previousPath);
    }
}

/// <summary>
/// Tests that change the test process's <c>PATH</c>, which decides what every other test's process
/// starts: they run alone, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerPathCollection
{
    public const string Name = "Server process PATH";
}
