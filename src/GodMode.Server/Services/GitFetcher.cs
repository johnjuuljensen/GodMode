using System.Diagnostics;

namespace GodMode.Server.Services;

/// <summary>
/// Shallow clones or fetches git repos for manifest convergence.
/// </summary>
public static class GitFetcher
{
    /// <summary>
    /// Shallow clones a git repo to a target directory.
    /// If the directory already exists, pulls latest instead.
    /// </summary>
    public static async Task CloneOrPullAsync(string gitUrl, string targetDir, string? gitRef = null)
    {
        if (Directory.Exists(Path.Combine(targetDir, ".git")))
        {
            await RunGitAsync(targetDir, "pull --ff-only");
        }
        else
        {
            var args = gitRef != null
                ? $"clone --depth 1 --branch {gitRef} {gitUrl} {targetDir}"
                : $"clone --depth 1 {gitUrl} {targetDir}";
            await RunGitAsync(null, args);
        }
    }

    private static async Task RunGitAsync(string? workingDir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? ""
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git.");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {stderr}");
        }
    }
}
