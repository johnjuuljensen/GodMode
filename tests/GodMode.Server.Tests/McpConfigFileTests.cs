using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// The MCP config carries the MCP servers' credentials: it is written into the project's
/// .godmode folder rather than the shared temp directory, owner-only where the OS has modes,
/// and deleted when the process that used it is gone.
/// </summary>
public class McpConfigFileTests
{
    [Fact]
    public void Write_PutsTheConfigInTheProjectsGodModeFolder()
    {
        var workDir = ServerProcess.CreateWorkDir("mcpcfg");
        try
        {
            var path = McpConfigFile.Write(workDir, """{"mcpServers":{}}""");

            Assert.Equal(Path.Combine(workDir, ".godmode", "mcp-config.json"), path);
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
            McpConfigFile.Write(workDir, "old");
            var path = McpConfigFile.Write(workDir, "new");

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
            var path = McpConfigFile.Write(workDir, "{}");
            var launchedAt = DateTime.UtcNow;

            McpConfigFile.DeleteIfWrittenBefore(workDir, launchedAt);

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
            var path = McpConfigFile.Write(workDir, "{}");
            File.SetLastWriteTimeUtc(path, launchedAt.AddSeconds(1));

            McpConfigFile.DeleteIfWrittenBefore(workDir, launchedAt);

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
            McpConfigFile.DeleteIfWrittenBefore(workDir, DateTime.UtcNow);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }
}
