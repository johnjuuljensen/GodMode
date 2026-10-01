using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// The MCP config a launch gets is written into the session's state folder rather than the
/// shared temp directory, owner-only where the OS has modes, and deleted when the process that
/// used it is gone.
/// </summary>
public class McpConfigFileTests
{
    [Fact]
    public void Write_PutsTheConfigInTheSessionsStateFolder()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            var path = McpConfigFile.WriteFile(Path.Combine(workDir, McpConfigFile.FileName), """{"mcpServers":{}}""");

            Assert.Equal(Path.Combine(workDir, "mcp-config.json"), path);
            Assert.Equal("""{"mcpServers":{}}""", File.ReadAllText(path));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Write_ReplacesAnEarlierConfig_OwnerOnlyOnUnix()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            McpConfigFile.WriteFile(Path.Combine(workDir, McpConfigFile.FileName), "old");
            var path = McpConfigFile.WriteFile(Path.Combine(workDir, McpConfigFile.FileName), "new");

            Assert.Equal("new", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void DeleteIfWrittenBefore_DeletesTheConfigOfThatLaunch()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            var path = McpConfigFile.WriteFile(Path.Combine(workDir, McpConfigFile.FileName), "{}");
            var launchedAt = DateTime.UtcNow;

            McpConfigFile.DeleteFileIfWrittenBefore(Path.Combine(workDir, McpConfigFile.FileName), launchedAt);

            Assert.False(File.Exists(path));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void DeleteIfWrittenBefore_KeepsTheConfigOfANewerLaunch()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            var launchedAt = DateTime.UtcNow;
            var path = McpConfigFile.WriteFile(Path.Combine(workDir, McpConfigFile.FileName), "{}");
            File.SetLastWriteTimeUtc(path, launchedAt.AddSeconds(1));

            McpConfigFile.DeleteFileIfWrittenBefore(Path.Combine(workDir, McpConfigFile.FileName), launchedAt);

            Assert.True(File.Exists(path));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void DeleteIfWrittenBefore_NoConfig_DoesNothing()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            McpConfigFile.DeleteFileIfWrittenBefore(Path.Combine(workDir, McpConfigFile.FileName), DateTime.UtcNow);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
