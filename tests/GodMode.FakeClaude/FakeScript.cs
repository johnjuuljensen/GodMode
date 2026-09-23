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
}

/// <summary>One step of a fake claude script.</summary>
public abstract record ScriptStep
{
    /// <summary>Writes one line to stdout. <c>{{session_id}}</c> is replaced by the session id from argv.</summary>
    public sealed record Emit(string Line) : ScriptStep;

    /// <summary>Waits for the next stdin line. Exits 0 if stdin closes first, as the real CLI does.</summary>
    public sealed record AwaitStdin : ScriptStep;

    public sealed record Sleep(int Milliseconds) : ScriptStep;

    /// <summary>Writes one line to stderr.</summary>
    public sealed record Stderr(string Text) : ScriptStep;

    public sealed record Exit(int Code) : ScriptStep;
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

    /// <summary>One whole turn: wait for the prompt, answer with <paramref name="answer"/>, end the turn.</summary>
    public FakeScript Turn(string answer) =>
        AwaitStdin().EmitAssistant(answer).EmitResult();

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
                _ => throw new FormatException($"Unknown fake claude script step: {raw}"),
            });
        }
        return script;
    }
}
