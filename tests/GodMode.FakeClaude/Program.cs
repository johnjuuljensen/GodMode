// Stands in for the `claude` CLI in GodMode's lifecycle tests: plays a scripted stream-json
// conversation on stdout, asks for permission as claude does (an MCP client on the server its
// --mcp-config names for its --permission-prompt-tool), and records its argv, environment, MCP
// config, stdin, permission answers and exit code to a sidecar.
// Every other CLI flag GodMode passes is accepted and ignored; --session-id / --resume only feed
// the {{session_id}} placeholder.
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GodMode.FakeClaude;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
// Read at start, as claude reads it: the process manager deletes the file when the process exits
var mcpConfig = ArgValue("--mcp-config") is { } mcpConfigPath && File.Exists(mcpConfigPath) ? File.ReadAllText(mcpConfigPath) : null;
FakeRecording.Append(recordPath, new RecordLine(RecordLine.Start, pid, Argv: args, Environment: environment, McpConfig: mcpConfig));
McpClient? permissionServer = null;

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
            FakeRecording.Append(recordPath, new RecordLine(RecordLine.Permission, pid, Line: await AskPermissionAsync(ask)));
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

// What claude does with its --permission-prompt-tool (mcp__<server>__<tool>): calls the tool on that
// server of its --mcp-config, and hands the tool's text back as the answer, however long it takes
async Task<string> AskPermissionAsync(ScriptStep.AskPermission ask)
{
    using var cancel = new CancellationTokenSource();
    var progress = new RecordingProgress(value =>
    {
        FakeRecording.Append(recordPath, new RecordLine(RecordLine.Progress, pid, Line: value.Message));
        if (ask.CancelOnProgress) cancel.Cancel();
    });
    var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ask.Arguments)!
        .ToDictionary(argument => argument.Key, argument => (object?)argument.Value);
    try
    {
        var (server, tool) = PermissionPromptTool();
        permissionServer ??= await ConnectAsync(server);
        var result = await permissionServer.CallToolAsync(tool, arguments, progress, cancellationToken: cancel.Token);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return result.IsError == true ? $"error: {text}" : text;
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
        return "cancelled";
    }
    catch (Exception ex)
    {
        return $"error: {ex.GetType().Name}: {ex.Message}";
    }
}

// The MCP server and tool --permission-prompt-tool names: mcp__godmode__permission_prompt
(string Server, string Tool) PermissionPromptTool() =>
    ArgValue("--permission-prompt-tool")?.Split("__") is ["mcp", var server, var tool]
        ? (server, tool)
        : throw new InvalidOperationException("no --permission-prompt-tool mcp__<server>__<tool>");

// A streamable HTTP client on the server's entry in the MCP config (its url and headers), listing
// its tools first as claude does when it connects
async Task<McpClient> ConnectAsync(string server)
{
    using var config = JsonDocument.Parse(mcpConfig ?? throw new InvalidOperationException("no --mcp-config"));
    var entry = config.RootElement.GetProperty("mcpServers").GetProperty(server);
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = server,
        Endpoint = new Uri(entry.GetProperty("url").GetString()!),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = entry.TryGetProperty("headers", out var headers)
            ? headers.EnumerateObject().ToDictionary(header => header.Name, header => header.Value.GetString() ?? "")
            : null,
    }, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, ownsHttpClient: true);
    var client = await McpClient.CreateAsync(transport);
    var tools = await client.ListToolsAsync();
    FakeRecording.Append(recordPath, new RecordLine(RecordLine.Tools, pid, Line: JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool))));
    return client;
}

string? ArgValue(string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Reports progress as it arrives, on the thread that received it (<see cref="Progress{T}"/> posts it elsewhere, later).</summary>
internal sealed class RecordingProgress(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
{
    public void Report(ProgressNotificationValue value) => report(value);
}
