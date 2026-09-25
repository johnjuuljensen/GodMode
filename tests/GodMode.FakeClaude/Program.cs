// Stands in for the `claude` CLI in GodMode's lifecycle tests: plays a scripted stream-json
// conversation on stdout, asks for permission as claude does (an MCP client on the server its
// --mcp-config names for its --permission-prompt-tool), answers an interrupt as claude does, and
// records its argv, environment, MCP config, stdin, permission answers, interrupts, children and
// exit code to a sidecar.
// Every other CLI flag GodMode passes is accepted and ignored; --session-id / --resume only feed
// the {{session_id}} placeholder.
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GodMode.FakeClaude;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// A child a launch started (spawn-child): it outlives any interrupt, and only a kill ends it
if (args is [FakeClaudeEnvironment.ChildFlag, ..])
{
    using var ignoreInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
    using var ignoreQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

// Leaves a child behind (spawn-detached): records it for the launch, and exits before it
if (args is [FakeClaudeEnvironment.DetachFlag, var detachRecord, var launchPid])
{
    FakeRecording.Append(detachRecord, new RecordLine(RecordLine.Child, int.Parse(launchPid), Line: StartChild().ToString()));
    return 0;
}

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

// Consecutive emit steps go out in one write, as a burst of output from the real CLI does. One
// writer at a time: the script, or the interrupt's answer.
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
var stdoutLock = new SemaphoreSlim(1, 1);
var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

// An interrupt, as claude takes one (Ctrl+C or Ctrl+Break on Windows, SIGINT or SIGQUIT elsewhere),
// until the script says to ignore them. Handled here: the runtime's default would end the process
// before it is recorded
var ignoringInterrupts = 0;
var inTurn = 0;
var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
void OnInterrupt(PosixSignalContext context)
{
    context.Cancel = true;
    FakeRecording.Append(recordPath, new RecordLine(RecordLine.Interrupt, pid, Line: context.Signal.ToString()));
    if (Volatile.Read(ref ignoringInterrupts) == 0) interrupted.TrySetResult();
}
using var onSigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnInterrupt);
using var onSigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnInterrupt);

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

var played = PlayAsync();
if (await Task.WhenAny(played, interrupted.Task) == played) return await played;

// What claude does with an interrupt in a turn: the turn ends as interrupted. Then it exits 0
await stdoutLock.WaitAsync();
if (Volatile.Read(ref inTurn) == 1)
    foreach (var step in new FakeScript().EmitUser("[Request interrupted by user]").EmitResult("", isError: true).Steps.OfType<ScriptStep.Emit>())
        await stdout.WriteLineAsync(step.Line.Replace(FakeScript.SessionIdPlaceholder, sessionId));
await stdout.FlushAsync();
return Exit(0);

async Task<int> PlayAsync()
{
    for (var i = 0; i < script.Steps.Count; i++)
    {
        switch (script.Steps[i])
        {
            case ScriptStep.Emit emit:
                await stdoutLock.WaitAsync();
                try
                {
                    await stdout.WriteLineAsync(emit.Line.Replace(FakeScript.SessionIdPlaceholder, sessionId));
                    if (i + 1 == script.Steps.Count || script.Steps[i + 1] is not ScriptStep.Emit)
                        await stdout.FlushAsync();
                    if (emit.Line.Contains("\"type\":\"result\"")) Volatile.Write(ref inTurn, 0);
                }
                finally { stdoutLock.Release(); }
                break;
            case ScriptStep.AwaitStdin:
                if (!await stdinLines.Reader.WaitToReadAsync()) return Exit(0);
                stdinLines.Reader.TryRead(out _);
                Volatile.Write(ref inTurn, 1);
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
            case ScriptStep.IgnoreInterrupt:
                Volatile.Write(ref ignoringInterrupts, 1);
                break;
            case ScriptStep.SpawnChild { Detached: false }:
                FakeRecording.Append(recordPath, new RecordLine(RecordLine.Child, pid, Line: StartChild().ToString()));
                break;
            case ScriptStep.SpawnChild { Detached: true }:
                using (var detaching = Process.Start(ChildStart(FakeClaudeEnvironment.DetachFlag, recordPath, pid.ToString()))!)
                    await detaching.WaitForExitAsync();
                break;
        }
    }

    // Off the end of the script: idle between turns until stdin closes, like the real CLI.
    while (await stdinLines.Reader.WaitToReadAsync())
        while (stdinLines.Reader.TryRead(out _)) { }
    return Exit(0);
}

int Exit(int code)
{
    FakeRecording.Append(recordPath, new RecordLine(RecordLine.Exited, pid, Code: code));
    return code;
}

// Another copy of the fake, as a child with its own pipes: it holds none of this process's
static ProcessStartInfo ChildStart(params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

static int StartChild()
{
    using var child = Process.Start(ChildStart(FakeClaudeEnvironment.ChildFlag))!;
    return child.Id;
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
