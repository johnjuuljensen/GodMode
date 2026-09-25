using System.Text;
using System.Text.Json;

namespace GodMode.FakeClaude;

/// <summary>
/// Where the fake finds its script and sidecar: an argument (for example via a root's
/// <c>claudeArgs</c>) or, failing that, an environment variable (via a root's <c>environment</c>).
/// Paths may be relative; the fake resolves them against its working directory, which GodMode
/// sets to the project folder.
/// </summary>
public static class FakeClaudeEnvironment
{
    /// <summary>Path of the script to play (<see cref="FakeScript"/> text format).</summary>
    public const string Script = "GODMODE_FAKE_CLAUDE_SCRIPT";

    /// <summary>Path of the sidecar the fake appends its argv, environment, stdin and exit to.</summary>
    public const string Record = "GODMODE_FAKE_CLAUDE_RECORD";

    public const string ScriptFlag = "--fake-script";
    public const string RecordFlag = "--fake-record";

    /// <summary>
    /// The fake started as a child of a launch (<see cref="ScriptStep.SpawnChild"/>): it ignores
    /// interrupts and sleeps until it is killed. Followed by the sidecar's path and the launch's pid.
    /// </summary>
    public const string ChildFlag = "--fake-child";

    /// <summary>
    /// The fake started to leave a child behind (<see cref="ScriptStep.SpawnChild"/> with
    /// <c>Detached</c>): it starts a <see cref="ChildFlag"/> child and exits at once, so the child's
    /// parent is gone. Followed by the sidecar's path and the launch's pid.
    /// </summary>
    public const string DetachFlag = "--fake-detach";

    /// <summary>After <see cref="ChildFlag"/>: the child leads a session of its own (Linux).</summary>
    public const string OwnSessionFlag = "--own-session";

    /// <summary>
    /// Windows: raises Ctrl+C (<c>ctrl-c</c>) or Ctrl+Break (<c>ctrl-break</c>) in the console of the
    /// process whose id follows, as that key pressed in its terminal would, and exits 0 if it did.
    /// A process can only raise console events in its own console, so a test borrows this one's.
    /// </summary>
    public const string RaiseFlag = "--fake-raise";
}

/// <summary>One step of a fake claude script.</summary>
public abstract record ScriptStep
{
    /// <summary>
    /// Writes one line to stdout. <c>{{session_id}}</c> is replaced by the session id from argv.
    /// Consecutive emits are flushed together, so the server reads them as one burst.
    /// </summary>
    public sealed record Emit(string Line) : ScriptStep;

    /// <summary>Waits for the next stdin line. Exits 0 if stdin closes first, as the real CLI does.</summary>
    public sealed record AwaitStdin : ScriptStep;

    public sealed record Sleep(int Milliseconds) : ScriptStep;

    /// <summary>Writes one line to stderr.</summary>
    public sealed record Stderr(string Text) : ScriptStep;

    public sealed record Exit(int Code) : ScriptStep;

    /// <summary>
    /// When launched with <c>--resume</c>, fails as the real CLI does for a session it has no
    /// conversation for: the error on stderr, exit 1. Otherwise does nothing.
    /// </summary>
    public sealed record RejectResume : ScriptStep;

    /// <summary>
    /// Asks for permission as claude does with its <c>--permission-prompt-tool</c>: an MCP client
    /// on the server its <c>--mcp-config</c> names for that tool (<c>tools/list</c> on its first
    /// call, then <c>tools/call</c>), with the headers the config gives. Waits for the answer however
    /// long it takes and records it. <paramref name="Arguments"/> is the tool call's arguments as
    /// claude sends them: <c>{"tool_name":…,"input":{…},"tool_use_id":…}</c>. With
    /// <paramref name="CancelOnProgress"/>, cancels the call when the server first reports progress,
    /// as claude does when the user interrupts the turn, and records <c>cancelled</c>.
    /// </summary>
    public sealed record AskPermission(string Arguments, bool CancelOnProgress = false) : ScriptStep;

    /// <summary>
    /// From here on an interrupt is recorded and otherwise ignored. Until this step the fake does
    /// what claude does with one (Ctrl+C or Ctrl+Break on Windows, SIGINT or SIGQUIT elsewhere): in
    /// a turn, it writes the interrupted turn's end, then it exits 0, whatever step it is on.
    /// </summary>
    public sealed record IgnoreInterrupt : ScriptStep;

    /// <summary>
    /// Starts a child process that ignores interrupts and sleeps until killed, and records its pid.
    /// <paramref name="Detached"/> starts it through a process that exits at once, so the child's
    /// parent is gone: it is re-parented, and only its process group or Job Object still holds it.
    /// </summary>
    /// <paramref name="OwnSession"/> starts it as the leader of a session of its own (Linux: setsid), as a
    /// detached spawn or a daemon does: it is out of the process group, and only a walk of the tree
    /// finds it while its parent lives. A Job Object holds it on Windows all the same.
    public sealed record SpawnChild(bool Detached = false, bool OwnSession = false) : ScriptStep;
}

/// <summary>
/// A script for <c>GodMode.FakeClaude</c>. One step per line, <c>verb argument</c>:
/// <code>
/// # comment
/// emit {"type":"system","subtype":"init","session_id":"{{session_id}}"}
/// await-stdin
/// sleep 100
/// stderr some text
/// exit 1
/// reject-resume
/// permission {"tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_1"}
/// permission-cancel {"tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_1"}
/// ignore-interrupt
/// spawn-child
/// spawn-detached
/// spawn-own-session
/// </code>
/// Blank lines and lines starting with <c>#</c> are ignored. A script that runs off its end keeps
/// the process alive until stdin closes (then exits 0), like the real CLI between turns.
/// Tests build scripts with the fluent methods and write them with <see cref="Save"/>; the fake
/// reads them with <see cref="Load"/>, so the format lives in this one file.
/// </summary>
public sealed class FakeScript
{
    public const string SessionIdPlaceholder = "{{session_id}}";

    private readonly List<ScriptStep> _steps = [];

    public IReadOnlyList<ScriptStep> Steps => _steps;

    public FakeScript Add(ScriptStep step) { _steps.Add(step); return this; }
    public FakeScript Emit(string jsonLine) => Add(new ScriptStep.Emit(jsonLine));
    public FakeScript AwaitStdin() => Add(new ScriptStep.AwaitStdin());
    public FakeScript Sleep(int milliseconds) => Add(new ScriptStep.Sleep(milliseconds));
    public FakeScript Stderr(string text) => Add(new ScriptStep.Stderr(text));
    public FakeScript Exit(int code) => Add(new ScriptStep.Exit(code));
    public FakeScript RejectResume() => Add(new ScriptStep.RejectResume());
    public FakeScript AskPermission(string toolName, object input, string toolUseId = "toolu_fake") =>
        Add(new ScriptStep.AskPermission(Json(new { tool_name = toolName, input, tool_use_id = toolUseId })));

    /// <summary>As <see cref="AskPermission"/>, cancelling the call once the server reports progress on it.</summary>
    public FakeScript AskPermissionAndCancel(string toolName, object input, string toolUseId = "toolu_fake") =>
        Add(new ScriptStep.AskPermission(Json(new { tool_name = toolName, input, tool_use_id = toolUseId }), CancelOnProgress: true));

    public FakeScript IgnoreInterrupt() => Add(new ScriptStep.IgnoreInterrupt());
    public FakeScript SpawnChild(bool detached = false, bool ownSession = false) => Add(new ScriptStep.SpawnChild(detached, ownSession));

    /// <summary>What the real CLI writes to stderr when <c>--resume</c> names a session it has no conversation for.</summary>
    public const string NoConversationError = "No conversation found with session ID: ";

    // ── Stream-json lines in the shape the real CLI emits (only the fields GodMode reads) ──

    /// <summary><c>system/init</c> carrying the session id GodMode passed on the command line.</summary>
    public FakeScript EmitInit() =>
        Emit($$"""{"type":"system","subtype":"init","session_id":"{{SessionIdPlaceholder}}"}""");

    /// <summary>The user message echoed back by <c>--replay-user-messages</c>.</summary>
    public FakeScript EmitUser(string text) =>
        Emit(Json(new { type = "user", message = new { role = "user", content = new[] { new { type = "text", text } } }, session_id = SessionIdPlaceholder }));

    public FakeScript EmitAssistant(string text) =>
        Emit(Json(new { type = "assistant", message = new { role = "assistant", content = new[] { new { type = "text", text } } }, session_id = SessionIdPlaceholder }));

    public FakeScript EmitResult(string result = "done", bool isError = false) =>
        Emit(Json(new
        {
            type = "result",
            subtype = isError ? "error_during_execution" : "success",
            is_error = isError,
            result,
            duration_ms = 10,
            total_cost_usd = 0.0,
            usage = new { input_tokens = 1, output_tokens = 1 },
            session_id = SessionIdPlaceholder,
        }));

    /// <summary>
    /// One whole turn: wait for the prompt, answer with <paramref name="answer"/>, end the turn. The
    /// result follows after a short pause, so the turn does not race the server's per-line handlers;
    /// a test about that race emits the two lines back to back itself.
    /// </summary>
    public FakeScript Turn(string answer) =>
        AwaitStdin().EmitAssistant(answer).Sleep(50).EmitResult();

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    // ── Text format ──

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var step in _steps)
            text.AppendLine(step switch
            {
                ScriptStep.Emit e => $"emit {e.Line}",
                ScriptStep.AwaitStdin => "await-stdin",
                ScriptStep.Sleep s => $"sleep {s.Milliseconds}",
                ScriptStep.Stderr s => $"stderr {s.Text}",
                ScriptStep.Exit e => $"exit {e.Code}",
                ScriptStep.RejectResume => "reject-resume",
                ScriptStep.AskPermission { CancelOnProgress: true } p => $"permission-cancel {p.Arguments}",
                ScriptStep.AskPermission p => $"permission {p.Arguments}",
                ScriptStep.IgnoreInterrupt => "ignore-interrupt",
                ScriptStep.SpawnChild { OwnSession: true } => "spawn-own-session",
                ScriptStep.SpawnChild { Detached: true } => "spawn-detached",
                ScriptStep.SpawnChild => "spawn-child",
                _ => throw new InvalidOperationException($"Unknown step {step}"),
            });
        return text.ToString();
    }

    public void Save(string path) => File.WriteAllText(path, ToString());

    public static FakeScript Load(string path) => Parse(File.ReadAllLines(path));

    public static FakeScript Parse(IEnumerable<string> lines)
    {
        var script = new FakeScript();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var space = line.IndexOf(' ');
            var (verb, argument) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
            script.Add(verb switch
            {
                "emit" => new ScriptStep.Emit(argument),
                "await-stdin" => new ScriptStep.AwaitStdin(),
                "sleep" => new ScriptStep.Sleep(int.Parse(argument)),
                "stderr" => new ScriptStep.Stderr(argument),
                "exit" => new ScriptStep.Exit(int.Parse(argument)),
                "reject-resume" => new ScriptStep.RejectResume(),
                "permission" => new ScriptStep.AskPermission(argument),
                "permission-cancel" => new ScriptStep.AskPermission(argument, CancelOnProgress: true),
                "ignore-interrupt" => new ScriptStep.IgnoreInterrupt(),
                "spawn-child" => new ScriptStep.SpawnChild(),
                "spawn-detached" => new ScriptStep.SpawnChild(Detached: true),
                "spawn-own-session" => new ScriptStep.SpawnChild(OwnSession: true),
                _ => throw new FormatException($"Unknown fake claude script step: {raw}"),
            });
        }
        return script;
    }
}
