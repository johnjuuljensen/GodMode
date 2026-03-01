using System.Diagnostics;
using System.Text;

namespace GodMode.Server.Services;

/// <summary>
/// Runs scripts as part of project creation workflow.
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
    private static readonly string[] UnixExtensions = [".sh"];

    private readonly ILogger<ScriptRunner> _logger;

    public ScriptRunner(ILogger<ScriptRunner> logger)
    {
        _logger = logger;
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
        StreamWriter? logWriter = null;
        if (logFilePath != null)
        {
            var logDir = Path.GetDirectoryName(logFilePath);
            if (logDir != null)
                Directory.CreateDirectory(logDir);
            logWriter = new StreamWriter(logFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
        }

        try
        {
            foreach (var script in scripts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunScriptAsync(script, rootPath, workingDirectory, environment, onProgress, logWriter, cancellationToken);
            }
        }
        finally
        {
            if (logWriter != null)
                await logWriter.DisposeAsync();
        }
    }

    private async Task RunScriptAsync(
        string script,
        string rootPath,
        string workingDirectory,
        Dictionary<string, string> environment,
        Func<string, Task> onProgress,
        StreamWriter? logWriter,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath(script, rootPath);

        _logger.LogInformation("Running script: {Script}", scriptPath);
        await onProgress($"Running: {Path.GetFileName(scriptPath)}");
        await LogLineAsync(logWriter, $"[{DateTime.UtcNow:O}] === Running: {scriptPath} ===");
        await LogLineAsync(logWriter, $"[{DateTime.UtcNow:O}] Working directory: {workingDirectory}");

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

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<int>();

        process.Exited += (_, _) => exitTcs.TrySetResult(process.ExitCode);

        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data != null)
            {
                await LogLineAsync(logWriter, $"[stdout] {e.Data}");
                try { await onProgress(e.Data); }
                catch { /* swallow callback errors */ }
            }
        };

        var stderrLines = new List<string>();
        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data != null)
            {
                stderrLines.Add(e.Data);
                await LogLineAsync(logWriter, $"[stderr] {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        });

        var exitCode = await exitTcs.Task;

        await LogLineAsync(logWriter, $"[{DateTime.UtcNow:O}] Exit code: {exitCode}");

        if (exitCode != 0)
        {
            var stderr = string.Join(Environment.NewLine, stderrLines);
            var message = $"Script '{script}' exited with code {exitCode}";
            if (!string.IsNullOrEmpty(stderr))
                message += $": {stderr}";

            _logger.LogError("{Message}", message);
            await LogLineAsync(logWriter, $"[{DateTime.UtcNow:O}] FAILED: {message}");
            throw new InvalidOperationException(message);
        }

        _logger.LogInformation("Script completed: {Script}", scriptPath);
        await LogLineAsync(logWriter, $"[{DateTime.UtcNow:O}] Completed: {scriptPath}");
    }

    private static async Task LogLineAsync(StreamWriter? writer, string line)
    {
        if (writer != null)
        {
            try { await writer.WriteLineAsync(line); }
            catch { /* best effort — don't fail scripts over log I/O */ }
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

    private static (string FileName, string Args) GetShellCommand(string scriptPath)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => ("pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""),
            ".bat" or ".cmd" => ("cmd", $"/c \"{scriptPath}\""),
            ".sh" => ("bash", $"\"{scriptPath}\""),
            _ => ("bash", $"\"{scriptPath}\"")
        };
    }
}
