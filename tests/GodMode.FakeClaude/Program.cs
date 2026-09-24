// Stands in for the `claude` CLI in GodMode's lifecycle tests: plays a scripted stream-json
// conversation on stdout, asks for permission as the GodMode bridge does, and records its argv,
// environment, stdin, permission answers and exit code to a sidecar.
// Every CLI flag GodMode passes is accepted and ignored; --session-id / --resume only feed
// the {{session_id}} placeholder.
using System.Collections;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GodMode.FakeClaude;

var scriptPath = ArgValue(FakeClaudeEnvironment.ScriptFlag) ?? Environment.GetEnvironmentVariable(FakeClaudeEnvironment.Script);
var recordPath = ArgValue(FakeClaudeEnvironment.RecordFlag) ?? Environment.GetEnvironmentVariable(FakeClaudeEnvironment.Record);
if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(recordPath))
{
    Console.Error.WriteLine($"Error: set {FakeClaudeEnvironment.Script} and {FakeClaudeEnvironment.Record} " +
        $"(or pass {FakeClaudeEnvironment.ScriptFlag} and {FakeClaudeEnvironment.RecordFlag}).");
    return 2;
}
recordPath = Path.GetFullPath(recordPath);

var pid = Environment.ProcessId;
var environment = Environment.GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
FakeRecording.Append(recordPath, new RecordLine(RecordLine.Start, pid, Argv: args, Environment: environment));

var sessionId = ArgValue("--session-id") ?? ArgValue("--resume") ?? "";
var script = FakeScript.Load(Path.GetFullPath(scriptPath));

// Consecutive emit steps go out in one write, as a burst of output from the real CLI does.
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

// Every stdin line is recorded as it arrives, whether or not the script is waiting for one.
var stdinLines = Channel.CreateUnbounded<string>();
_ = Task.Run(async () =>
{
    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    while (await stdin.ReadLineAsync() is { } line)
    {
        FakeRecording.Append(recordPath, new RecordLine(RecordLine.Stdin, pid, Line: line));
        await stdinLines.Writer.WriteAsync(line);
    }
    stdinLines.Writer.Complete();
});

for (var i = 0; i < script.Steps.Count; i++)
{
    switch (script.Steps[i])
    {
        case ScriptStep.Emit emit:
            await stdout.WriteLineAsync(emit.Line.Replace(FakeScript.SessionIdPlaceholder, sessionId));
            if (i + 1 == script.Steps.Count || script.Steps[i + 1] is not ScriptStep.Emit)
                await stdout.FlushAsync();
            break;
        case ScriptStep.AwaitStdin:
            if (!await stdinLines.Reader.WaitToReadAsync()) return Exit(0);
            stdinLines.Reader.TryRead(out _);
            break;
        case ScriptStep.Sleep sleep:
            await Task.Delay(sleep.Milliseconds);
            break;
        case ScriptStep.Stderr text:
            await stderr.WriteLineAsync(text.Text);
            break;
        case ScriptStep.Exit exit:
            return Exit(exit.Code);
        case ScriptStep.AskPermission ask:
            FakeRecording.Append(recordPath, new RecordLine(RecordLine.Permission, pid, Line: await AskPermissionAsync(ask.Arguments)));
            break;
        case ScriptStep.RejectResume when ArgValue("--resume") is { } resumed:
            await stderr.WriteLineAsync(FakeScript.NoConversationError + resumed);
            return Exit(1);
    }
}

// Off the end of the script: idle between turns until stdin closes, like the real CLI.
while (await stdinLines.Reader.WaitToReadAsync())
    while (stdinLines.Reader.TryRead(out _)) { }
return Exit(0);

int Exit(int code)
{
    FakeRecording.Append(recordPath, new RecordLine(RecordLine.Exited, pid, Code: code));
    return code;
}

// What the bridge's permission_prompt does (src/GodMode.McpBridge): POST, wait, hand the answer back
static async Task<string> AskPermissionAsync(string arguments)
{
    using var args = JsonDocument.Parse(arguments);
    var root = args.RootElement;
    var body = JsonSerializer.Serialize(new
    {
        toolName = root.GetProperty("tool_name").GetString(),
        input = root.GetProperty("input"),
        toolUseId = root.TryGetProperty("tool_use_id", out var id) ? id.GetString() : null,
    });
    var serverUrl = Environment.GetEnvironmentVariable("GODMODE_SERVER_URL") ?? "http://localhost:31337";
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/internal/permission")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("GODMODE_PROJECT_TOKEN"));
    request.Headers.Add("X-GodMode-Project-Id", Environment.GetEnvironmentVariable("GODMODE_PROJECT_ID"));
    try
    {
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return response.IsSuccessStatusCode ? text : $"error: HTTP {(int)response.StatusCode} {text}";
    }
    catch (HttpRequestException ex)
    {
        return $"error: {ex.Message}";
    }
}

string? ArgValue(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
