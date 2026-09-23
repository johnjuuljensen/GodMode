using System.Net;
using System.Text;

namespace GodMode.Server.Tests;

/// <summary>
/// Webhooks and schedules were removed (IS#153). A roots directory created by an older server
/// may still hold their files; the server must start cleanly, not act on them, and leave them alone.
/// </summary>
public class LegacyWebhookScheduleFilesTests
{
    private const string WebhookKeyword = "nightly";
    private const string WebhookToken = "whk_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Shapes as the removed WebhookFileManager / ScheduleManager wrote them (camelCase, indented).
    private static readonly string WebhookJson = $$"""
        {
          "token": "{{WebhookToken}}",
          "profileName": "Default",
          "rootName": "missing-root",
          "actionName": "issue",
          "description": "Legacy webhook",
          "inputMapping": {
            "name": "issue.title",
            "prompt": "issue.body"
          },
          "enabled": true
        }
        """;

    private const string ScheduleJson = """
        {
          "description": "Legacy schedule",
          "enabled": true,
          "cron": "* * * * *",
          "target": {
            "rootName": "missing-root",
            "actionName": "freeform",
            "inputs": {
              "name": "nightly",
              "prompt": "do the thing"
            },
            "reuseProject": false
          }
        }
        """;

    private const string ProfileJson = """
        {
          "description": "Default profile"
        }
        """;

    [Fact]
    public async Task Server_StartsCleanly_AndIgnoresLegacyWebhookAndScheduleFiles()
    {
        var workDir = ServerProcess.CreateWorkDir("is153");
        var rootsDir = Path.Combine(workDir, "roots");
        var webhookPath = Path.Combine(rootsDir, ".webhooks", $"{WebhookKeyword}.json");
        var schedulePath = Path.Combine(rootsDir, ".profiles", "Default", "schedules", "nightly.json");
        var profilePath = Path.Combine(rootsDir, ".profiles", "Default", "profile.json");

        WriteFile(webhookPath, WebhookJson);
        WriteFile(schedulePath, ScheduleJson);
        WriteFile(profilePath, ProfileJson);

        var baseUrl = $"http://127.0.0.1:{ServerProcess.GetFreePort()}";

        // No API key on a loopback binding: the server runs without authentication.
        using (var server = ServerProcess.Start(workDir, baseUrl))
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            await server.WaitForHealthyAsync(http);

            // The webhook route is gone: posting to the legacy keyword with its valid token is
            // answered exactly like a POST to a path that never existed.
            using var unknown = await PostAsync(http, "/no-such-route");
            using var webhook = await PostAsync(http, $"/webhook/{WebhookKeyword}");
            Assert.Equal(unknown.StatusCode, webhook.StatusCode);

            // Still serving after all of the above.
            using var health = await http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(server.HasExited, $"Server exited unexpectedly.\n{server.Output}");
        }

        // The user's files are neither deleted nor rewritten.
        Assert.Equal(WebhookJson, File.ReadAllText(webhookPath));
        Assert.Equal(ScheduleJson, File.ReadAllText(schedulePath));
        Assert.Equal(ProfileJson, File.ReadAllText(profilePath));

        ServerProcess.DeleteWorkDir(workDir);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"issue":{"title":"t","body":"b"}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new("Bearer", WebhookToken);
        return http.SendAsync(request);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
