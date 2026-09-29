using System.Diagnostics;
using System.Text;

namespace GodMode.Server.Services;

/// <summary>
/// Runs a root's scripts (prepare, create, delete, status). A script's environment is the OS essentials
/// (<see cref="ChildEnvironment.Script"/>), then the configured environment the caller passes: never the
/// server's own, which holds its secrets.
///
/// Scripts can be specified with or without extension:
/// - With extension ("scripts/init.ps1") — used as-is.
/// - Without extension ("scripts/init") — resolved per OS:
///   Windows tries .ps1, .cmd, .bat; Linux/Mac tries .sh.
///   This lets the same config work on both platforms
///   when paired scripts (init.sh + init.ps1) live side by side.
/// </summary>
public class ScriptRunner : IScriptRunner
{
    private static readonly string[] WindowsExtensions = [".ps1", ".cmd", ".bat"];
    private static readonly string[] UnixExtensions = [".sh", ".ps1"];

    /// <summary>The stderr lines a script run for its output keeps for its error.</summary>
    private const int MaxStderrLines = 20;

    /// <summary>
    /// How long a script run for its effect waits after its exit for its stdout and stderr to end, so
    /// the lines it wrote last are logged and reported before the run goes on. It does not wait for
    /// them to end: a child the script started in the background can hold its pipes for as long as it lives.
    /// </summary>
    internal static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Configuration key for the PowerShell 7 executable that runs <c>.ps1</c> scripts (a name on PATH
    /// or a full path). The Docker image names its own, so a <c>pwsh</c> the session writes into a
    /// directory on PATH is never the one run.
    /// </summary>
    public const string PowerShellExecutableSetting = "PowerShell:Executable";

    private readonly ILogger<ScriptRunner> _logger;
    private readonly string _powerShell;

    public ScriptRunner(ILogger<ScriptRunner> logger, IConfiguration configuration)
    {
        _logger = logger;
        _powerShell = configuration[PowerShellExecutableSetting] is { Length: > 0 } powerShell ? powerShell : "pwsh";
    }

    public async Task RunAsync(
        string[] scripts,
        string rootPath,
        string workingDirectory,
        Dictionary<string, string> environment,
        Func<string, Task> onProgress,
        string? logFilePath = null,
        CancellationToken cancellationToken = default)
    {
        using var log = new ScriptLog(logFilePath);
        foreach (var script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scriptPath = ResolveScriptPath(script, rootPath);
            await onProgress($"Running: {Path.GetFileName(scriptPath)}");
            await RunScriptAsync(script, scriptPath, workingDirectory, environment, onProgress, log, forOutput: false, cancellationToken);
        }
    }

    public async Task<string> RunForOutputAsync(
        string script,
        string rootPath,
        string workingDirectory,
        Dictionary<string, string> environment,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath(script, rootPath);
        var output = new StringBuilder();
        using var tooMuch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task Collect(string line)
        {
            lock (output)
            {
                if (tooMuch.IsCancellationRequested) return Task.CompletedTask;
                if (output.Length + line.Length + 1 > maxOutputChars) tooMuch.Cancel();
                else output.Append(line).Append('\n');
            }
            return Task.CompletedTask;
        }

        try
        {
            await RunScriptAsync(script, scriptPath, workingDirectory, environment, Collect, ScriptLog.None, forOutput: true, tooMuch.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException($"Script '{script}' wrote more than {maxOutputChars} characters to stdout");
        }
        lock (output) return output.ToString();
    }

    /// <param name="forOutput">
    /// Run for its output, which is untrusted: wait for stdout to be read to its end, not only for the
    /// exit, and keep only the last <see cref="MaxStderrLines"/> lines of stderr for the error.
    /// </param>
    private async Task RunScriptAsync(
        string script,
        string scriptPath,
        string workingDirectory,
        Dictionary<string, string> environment,
        Func<string, Task> onProgress,
        ScriptLog log,
        bool forOutput,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running script: {Script}", scriptPath);
        log.WriteLine($"[{DateTime.UtcNow:O}] === Running: {scriptPath} ===");
        log.WriteLine($"[{DateTime.UtcNow:O}] Working directory: {workingDirectory}");

        var (fileName, args) = GetShellCommand(scriptPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Start from the allowlist, not the server's environment, which holds its secrets (the API key
        // among them), then the configured variables and the GODMODE_* ones
        startInfo.Environment.Clear();
        foreach (var (key, value) in ChildEnvironment.Script.Build(ChildEnvironment.Current(), environment))
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<int>();

        process.Exited += (_, _) => exitTcs.TrySetResult(process.ExitCode);

        // Each on its stream's own thread, which logs a line before it reads the next
        void OnStdout(string? line)
        {
            if (line != null)
            {
                log.WriteLine($"[stdout] {line}");
                _ = ReportAsync(onProgress, line);
            }
        }

        var stderrLines = new List<string>();
        void OnStderr(string? line)
        {
            if (line != null)
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                    if (forOutput && stderrLines.Count > MaxStderrLines) stderrLines.RemoveAt(0);
                }
                log.WriteLine($"[stderr] {line}");
            }
        }

        process.Start();
        // On threads of their own, not the thread pool's (ChildOutput)
        var read = Task.WhenAll(
            ChildOutput.ReadLinesAsync(process.StandardOutput, OnStdout, $"script {process.Id} stdout"),
            ChildOutput.ReadLinesAsync(process.StandardError, OnStderr, $"script {process.Id} stderr"));

        using var reg = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        });

        var exitCode = await exitTcs.Task;
        // Killed: the exit code says only that
        cancellationToken.ThrowIfCancellationRequested();
        if (forOutput) await read.WaitAsync(cancellationToken);
        else
        {
            try { await read.WaitAsync(DrainAfterExit, cancellationToken); }
            catch (TimeoutException) { _logger.LogDebug("Script {Script} exited with its output still open", scriptPath); }
        }

        log.WriteLine($"[{DateTime.UtcNow:O}] Exit code: {exitCode}");

        if (exitCode != 0)
        {
            string stderr;
            lock (stderrLines) stderr = string.Join(Environment.NewLine, stderrLines);
            var message = $"Script '{script}' exited with code {exitCode}";
            if (!string.IsNullOrEmpty(stderr))
                message += $": {stderr}";

            // Run for its output, the caller says what the failure means
            if (!forOutput) _logger.LogError("{Message}", message);
            log.WriteLine($"[{DateTime.UtcNow:O}] FAILED: {message}");
            throw new InvalidOperationException(message);
        }

        _logger.LogInformation("Script completed: {Script}", scriptPath);
        log.WriteLine($"[{DateTime.UtcNow:O}] Completed: {scriptPath}");
    }

    private static async Task ReportAsync(Func<string, Task> onProgress, string line)
    {
        try { await onProgress(line); }
        catch { /* swallow callback errors */ }
    }

    /// <summary>
    /// A run's log file, written from its scripts' stdout and stderr threads at once, and from the run:
    /// one line at a time under its lock, as a <see cref="StreamWriter"/> takes no two writes at once.
    /// Best effort: a script never fails over its log, and a line read once it is disposed (after
    /// <see cref="DrainAfterExit"/>) is dropped.
    /// </summary>
    private sealed class ScriptLog : IDisposable
    {
        /// <summary>No log: every line is dropped.</summary>
        public static readonly ScriptLog None = new(null);

        private readonly Lock _gate = new();
        private StreamWriter? _writer;

        public ScriptLog(string? path)
        {
            if (path == null) return;
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        }

        public void WriteLine(string line)
        {
            lock (_gate)
            {
                try { _writer?.WriteLine(line); }
                catch { /* best effort — don't fail scripts over log I/O */ }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _writer?.Dispose(); }
                catch { /* best effort */ }
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Resolves a script reference to an actual file path.
    /// If the path already has an extension and exists, use it directly.
    /// If extensionless, try platform-appropriate extensions.
    /// </summary>
    private string ResolveScriptPath(string script, string rootPath)
    {
        var basePath = Path.IsPathRooted(script)
            ? script
            : Path.Combine(rootPath, script);

        // If the exact path exists (has extension), use it
        if (File.Exists(basePath))
            return basePath;

        // Extensionless — try platform-appropriate extensions
        var extensions = OperatingSystem.IsWindows() ? WindowsExtensions : UnixExtensions;
        foreach (var ext in extensions)
        {
            var candidate = basePath + ext;
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved extensionless script '{Script}' to '{Resolved}'", script, candidate);
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Script not found: '{script}'. Tried: {basePath}, " +
            string.Join(", ", extensions.Select(e => basePath + e)));
    }

    private (string FileName, string Args) GetShellCommand(string scriptPath)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => (_powerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""),
            ".bat" or ".cmd" => ("cmd", $"/c \"{scriptPath}\""),
            ".sh" => ("bash", $"\"{scriptPath}\""),
            _ => ("bash", $"\"{scriptPath}\"")
        };
    }
}
