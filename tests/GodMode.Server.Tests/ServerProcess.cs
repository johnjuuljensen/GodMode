using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GodMode.Server.Tests;

/// <summary>
/// Launches the real GodMode.Server as a child process in its own work directory,
/// so tests exercise the shipped startup path (config, auth-mode selection, middleware order).
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private bool _disposed;

    public string WorkDir { get; }
    public string RootsDir { get; }

    private ServerProcess(string workDir, string rootsDir, Process process)
    {
        WorkDir = workDir;
        RootsDir = rootsDir;
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Output { get { lock (_output) return _output.ToString(); } }
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    /// <summary>Creates a fresh work directory (with a roots dir) for one server run.</summary>
    public static string CreateWorkDir(string prefix)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"godmode-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "roots"));
        return workDir;
    }

    /// <summary>
    /// Starts the server. Auth-related settings are always passed explicitly (empty when not given)
    /// and <c>CODESPACES</c> is cleared unless overridden, so a developer's environment cannot leak in.
    /// </summary>
    public static ServerProcess Start(
        string workDir,
        string urls,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var rootsDir = Path.Combine(workDir, "roots");
        var serverDll = Path.Combine(AppContext.BaseDirectory, "GodMode.Server.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? hostPath : "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(serverDll);
        psi.ArgumentList.Add($"--ProjectRootsDir={rootsDir}");
        psi.ArgumentList.Add($"--Urls={urls}");
        psi.ArgumentList.Add($"--Authentication:ApiKey={apiKey ?? ""}");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ASPNETCORE_URLS"] = "";
        psi.Environment["CODESPACES"] = "";
        psi.Environment["GITHUB_USER"] = "";
        if (environment != null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        return new ServerProcess(workDir, rootsDir, new Process { StartInfo = psi });
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The first address Kestrel reports it listens on: what a server bound to port 0 got.</summary>
    public async Task<string> WaitForListeningUrlAsync()
    {
        const string marker = "Now listening on: ";
        string? url = null;
        var found = await Lifecycle.LifecycleHarness.WaitForAsync(() =>
        {
            url = Output.Split('\n')
                .Select(line => line.IndexOf(marker, StringComparison.Ordinal) is var at and >= 0 ? line[(at + marker.Length)..].Trim() : null)
                .FirstOrDefault(listening => listening != null);
            return Task.FromResult(url != null || HasExited);
        }, TimeSpan.FromSeconds(60));
        Assert.True(found && url != null, $"Server did not report where it listens.\n{Output}");
        return url!;
    }

    public async Task WaitForHealthyAsync(HttpClient http)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(HasExited, $"Server exited during startup (code {(HasExited ? ExitCode : 0)}).\n{Output}");
            try
            {
                using var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        Assert.Fail($"Server did not become healthy within 60s.\n{Output}");
    }

    /// <summary>Waits for the process to exit on its own; returns false on timeout.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Kills the server with its children, as a crash would. A second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        _process.Dispose();
    }

    /// <summary>Best-effort removal of a work directory after the server has stopped.</summary>
    public static void DeleteWorkDir(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
