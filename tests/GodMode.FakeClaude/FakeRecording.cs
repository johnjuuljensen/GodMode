using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.FakeClaude;

/// <summary>
/// One line of the sidecar. Every launch appends a <c>start</c> line, then a <c>stdin</c> line per
/// line received, a <c>permission</c> line per permission prompt answered (the answer's JSON, or
/// <c>error: …</c> when the call failed), then an <c>exit</c> line if it exits on its own (a killed fake writes none).
/// </summary>
public sealed record RecordLine(
    string Kind,
    int Pid,
    IReadOnlyList<string>? Argv = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Line = null,
    int? Code = null)
{
    public const string Start = "start";
    public const string Stdin = "stdin";
    public const string Permission = "permission";
    public const string Exited = "exit";
}

/// <summary>What one launch of the fake saw and did.</summary>
public sealed record FakeLaunch(
    int Pid,
    IReadOnlyList<string> Argv,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Stdin,
    int? ExitCode,
    IReadOnlyList<string> Permissions)
{
    /// <summary>The value following <paramref name="flag"/> in argv, or null.</summary>
    public string? ArgValue(string flag)
    {
        var index = Argv.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < Argv.Count ? Argv[index + 1] : null;
    }
}

/// <summary>Writes and reads the fake's sidecar (JSON lines, append-only, one file for all launches).</summary>
public static class FakeRecording
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lock WriteLock = new();

    public static void Append(string path, RecordLine line)
    {
        var json = JsonSerializer.Serialize(line, Options) + "\n";
        lock (WriteLock)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
    }

    /// <summary>All launches recorded at <paramref name="path"/>, in start order. Empty if nothing was recorded yet.</summary>
    public static IReadOnlyList<FakeLaunch> Read(string path)
    {
        if (!File.Exists(path)) return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<RecordLine>();
        while (reader.ReadLine() is { } text)
        {
            // The last line can be half-written while the fake is running; it is complete next read.
            try { if (JsonSerializer.Deserialize<RecordLine>(text, Options) is { } line) lines.Add(line); }
            catch (JsonException) { }
        }

        return lines
            .Where(l => l.Kind == RecordLine.Start)
            .Select(start =>
            {
                var own = lines.Where(l => l.Pid == start.Pid).ToList();
                return new FakeLaunch(
                    start.Pid,
                    start.Argv ?? [],
                    start.Environment ?? new Dictionary<string, string>(),
                    own.Where(l => l.Kind == RecordLine.Stdin).Select(l => l.Line ?? "").ToList(),
                    own.LastOrDefault(l => l.Kind == RecordLine.Exited)?.Code,
                    own.Where(l => l.Kind == RecordLine.Permission).Select(l => l.Line ?? "").ToList());
            })
            .ToList();
    }
}
