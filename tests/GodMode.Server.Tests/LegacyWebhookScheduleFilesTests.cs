using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
        var workDir = Path.Combine(Path.GetTempPath(), "godmode-is153-" + Guid.NewGuid().ToString("N"));
        var rootsDir = Path.Combine(workDir, "roots");
        var webhookPath = Path.Combine(rootsDir, ".webhooks", $"{WebhookKeyword}.json");
        var schedulePath = Path.Combine(rootsDir, ".profiles", "Default", "schedules", "nightly.json");
        var profilePath = Path.Combine(rootsDir, ".profiles", "Default", "profile.json");

        WriteFile(webhookPath, WebhookJson);
        WriteFile(schedulePath, ScheduleJson);
        WriteFile(profilePath, ProfileJson);

        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();

        using var server = StartServer(workDir, rootsDir, baseUrl, output);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            await WaitForHealthyAsync(http, server, output);

            // The webhook route is gone: posting to the legacy keyword with its valid token does nothing.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhook/{WebhookKeyword}")
            {
                Content = new StringContent("""{"issue":{"title":"t","body":"b"}}""", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new("Bearer", WebhookToken);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            // Still serving after all of the above.
            using var health = await http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(server.HasExited, $"Server exited unexpectedly.\n{output}");
        }
        finally
        {
            StopServer(server);
        }

        // The user's files are neither deleted nor rewritten.
        Assert.Equal(WebhookJson, File.ReadAllText(webhookPath));
        Assert.Equal(ScheduleJson, File.ReadAllText(schedulePath));
        Assert.Equal(ProfileJson, File.ReadAllText(profilePath));

        try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartServer(string workDir, string rootsDir, string url, StringBuilder output)
    {
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
        psi.ArgumentList.Add($"--Urls={url}");
        psi.ArgumentList.Add("--Authentication:Google:AllowedEmail=");
        psi.ArgumentList.Add("--Authentication:ApiKey=");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["CODESPACES"] = "";

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForHealthyAsync(HttpClient http, Process server, StringBuilder output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(server.HasExited, $"Server exited during startup (code {(server.HasExited ? server.ExitCode : 0)}).\n{output}");
            try
            {
                using var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        Assert.Fail($"Server did not become healthy within 60s.\n{output}");
    }

    private static void StopServer(Process server)
    {
        if (server.HasExited) return;
        server.Kill(entireProcessTree: true);
        server.WaitForExit(10_000);
    }
}
