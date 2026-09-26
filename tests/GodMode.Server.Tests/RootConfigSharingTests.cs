using System.Text;
using GodMode.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// The host edits a root's config by hand on a live server, which reads it in the background (a
/// pull request check on each move to Idle or Stopped). On Windows two opens of one file each have
/// to share what the other does, whichever came first: a save holding the file open for writing,
/// as an editor or <c>File.WriteAllText</c> does, and the server's read of it.
/// </summary>
public class RootConfigSharingTests
{
    [Theory]
    [InlineData("config.json", """{ "description": "Saved" }""")]
    [InlineData("config.work.json", """{ "description": "Saved" }""")]
    [InlineData("schema.json", """{ "type": "object", "title": "Saved" }""")]
    public void AFileOpenForWriting_IsRead(string file, string content)
    {
        var root = ServerProcess.CreateWorkDir("sharingcfg");
        try
        {
            var dir = Path.Combine(root, ".godmode-root");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """{ "description": "The root" }""");

            // As File.WriteAllText opens it: writing, and letting others read
            using var save = new FileStream(Path.Combine(dir, file), FileMode.Create, FileAccess.Write, FileShare.Read);
            save.Write(Encoding.UTF8.GetBytes(content));
            save.Flush();

            var action = Assert.Single(new RootConfigReader(NullLogger<RootConfigReader>.Instance).ReadConfigStrict(root).GetEffectiveActions());

            Assert.Equal("Saved", file == "schema.json" ? action.InputSchema?.GetProperty("title").GetString() : action.Description);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }
}
