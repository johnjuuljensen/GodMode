using GodMode.Server.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary><c>resumeOnRestart</c> and <c>resumePrompt</c> merge as the other scalar keys: an action's overlay replaces the base.</summary>
public class RootConfigResumeTests
{
    private static RootConfig Read(params (string File, string Json)[] files)
    {
        var root = ServerProcess.CreateWorkDir("resumecfg");
        try
        {
            var dir = Path.Combine(root, ".godmode-root");
            Directory.CreateDirectory(dir);
            foreach (var (file, json) in files) File.WriteAllText(Path.Combine(dir, file), json);
            return new RootConfigReader(NullLogger<RootConfigReader>.Instance).ReadConfigStrict(root);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }

    [Fact]
    public void Unset_ResumesWithTheDefaultPrompt()
    {
        var action = Read(("config.json", "{}")).ResolveAction(null)!;

        Assert.True(action.ResumeOnRestart);
        Assert.Equal(CreateAction.DefaultResumePrompt, action.ResumePrompt);
    }

    [Fact]
    public void Overlay_ReplacesTheBase_KeyByKey()
    {
        var config = Read(
            ("config.json", """{ "resumeOnRestart": false, "resumePrompt": "Base prompt." }"""),
            ("config.issue.json", """{ "resumeOnRestart": true }"""),
            ("config.freeform.json", """{ "resumePrompt": "Freeform prompt." }"""));

        var issue = config.ResolveAction("issue")!;
        var freeform = config.ResolveAction("freeform")!;
        Assert.Equal((true, "Base prompt."), (issue.ResumeOnRestart, issue.ResumePrompt));
        Assert.Equal((false, "Freeform prompt."), (freeform.ResumeOnRestart, freeform.ResumePrompt));
    }
}
