using System.Runtime.CompilerServices;
using System.Text;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// A project's <c>output.jsonl</c>: one JSON line per line of claude's output, each ended by
/// <c>\n</c>, appended only by the project's consumer. An offset is a byte position in the file;
/// a line's offset is the position just after its <c>\n</c>, which is where a client that has seen
/// it resumes. Files written before offsets existed end their lines with <c>\r\n</c>; they read
/// the same, with each offset still after the <c>\n</c>.
/// </summary>
public static class OutputLog
{
    /// <summary>At most this many lines go in one replayed batch.</summary>
    public const int MaxBatchLines = 1000;

    /// <summary>A batch is closed once its lines reach this many bytes (a single longer line is sent on its own).</summary>
    public const int MaxBatchBytes = 1 << 20;

    private const byte NewLine = (byte)'\n';
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string PathOf(string projectPath) => Path.Combine(projectPath, ".godmode", "output.jsonl");

    /// <summary>
    /// The offset after the file's last complete line: what a client that has seen everything
    /// resumes from. A trailing line without its <c>\n</c> (a write cut short) is not counted.
    /// </summary>
    public static long End(string projectPath)
    {
        var path = PathOf(projectPath);
        if (!File.Exists(path)) return 0;
        using var stream = OpenRead(path);
        return LastLineEnd(stream);
    }

    /// <summary>
    /// Where a subscription asking for <paramref name="fromOffset"/> starts:
    /// <list type="bullet">
    /// <item>0 or a line boundary: there.</item>
    /// <item>Inside a line: the next line boundary.</item>
    /// <item>Past the end: 0. The offset is not from this file (the project was recreated, or the file replaced), so all of it is replayed.</item>
    /// <item>Negative, <c>-N</c>: the last N turns. That is after the Nth-last <c>result</c> line, not counting one that ends the file, or 0 if there are fewer.</item>
    /// </list>
    /// </summary>
    public static async Task<long> StartAsync(string projectPath, long fromOffset, CancellationToken ct = default)
    {
        var path = PathOf(projectPath);
        if (fromOffset == 0 || !File.Exists(path)) return 0;

        await using var stream = OpenRead(path);
        if (fromOffset < 0) return await TailStartAsync(stream, -fromOffset, ct);
        if (fromOffset > stream.Length) return 0;

        stream.Position = fromOffset - 1;
        if (stream.ReadByte() == NewLine) return fromOffset;
        await foreach (var line in ReadLinesAsync(stream, ct))
            return line.Offset;
        return LastLineEnd(stream);
    }

    /// <summary>
    /// The complete lines from <paramref name="fromOffset"/> (a line boundary) to the end of the
    /// file as it is now, in batches of at most <see cref="MaxBatchLines"/> lines or about
    /// <see cref="MaxBatchBytes"/> bytes. Blank lines are skipped; a line still being written is not read.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<OutputLine>> ReadBatchesAsync(string projectPath, long fromOffset,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = PathOf(projectPath);
        if (!File.Exists(path)) yield break;

        await using var stream = OpenRead(path);
        if (fromOffset > stream.Length) yield break;
        stream.Position = fromOffset;

        var batch = new List<OutputLine>();
        var bytes = 0;
        await foreach (var line in ReadLinesAsync(stream, ct))
        {
            if (string.IsNullOrWhiteSpace(line.RawJson)) continue;
            batch.Add(line);
            bytes += line.RawJson.Length;
            if (batch.Count >= MaxBatchLines || bytes >= MaxBatchBytes)
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Opens the file to append lines, as the project's consumer does for each burst of output.</summary>
    public static Writer OpenWriter(string projectPath) => new(PathOf(projectPath));

    /// <summary>Appends lines and knows the offset after the last one.</summary>
    public sealed class Writer : IAsyncDisposable
    {
        private readonly FileStream _stream;

        internal Writer(string path)
        {
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            // A write cut short left a line without its \n: end it, so the next line starts on a boundary
            if (_stream.Length > 0 && LastByte(path) != NewLine)
            {
                _stream.WriteByte(NewLine);
                _stream.Flush();
            }
        }

        /// <summary>The offset after the last line in the file.</summary>
        public long Offset => _stream.Position;

        /// <summary>Appends <paramref name="line"/> and its <c>\n</c> and flushes it; returns the offset after it.</summary>
        public async Task<long> AppendAsync(string line)
        {
            var bytes = new byte[Utf8.GetByteCount(line) + 1];
            Utf8.GetBytes(line, bytes);
            bytes[^1] = NewLine;
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
            return _stream.Position;
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }

    // ── Reading ──

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);

    private static int LastByte(string path)
    {
        using var stream = OpenRead(path);
        if (stream.Length == 0) return -1;
        stream.Position = stream.Length - 1;
        return stream.ReadByte();
    }

    /// <summary>The offset after the last <c>\n</c> in the stream, or 0.</summary>
    private static long LastLineEnd(FileStream stream)
    {
        var buffer = new byte[64 * 1024];
        for (var end = stream.Length; end > 0;)
        {
            var start = Math.Max(0, end - buffer.Length);
            stream.Position = start;
            stream.ReadExactly(buffer, 0, (int)(end - start));
            var last = Array.LastIndexOf(buffer, NewLine, (int)(end - start) - 1);
            if (last >= 0) return start + last + 1;
            end = start;
        }
        return 0;
    }

    private static async Task<long> TailStartAsync(FileStream stream, long turns, CancellationToken ct)
    {
        // The ends of the last turns + 1 result lines; the extra one may be the file's last line
        var ends = new Queue<long>();
        long lastEnd = 0;
        await foreach (var line in ReadLinesAsync(stream, ct))
        {
            lastEnd = line.Offset;
            if (!IsResult(line.RawJson)) continue;
            ends.Enqueue(line.Offset);
            if (ends.Count > turns + 1) ends.Dequeue();
        }

        var boundaries = ends.Where(end => end != lastEnd).ToArray();
        return boundaries.Length >= turns ? boundaries[^(int)turns] : 0;
    }

    private static bool IsResult(string line) =>
        line.Contains("\"result\"", StringComparison.Ordinal) && ProjectLifecycle.ExtractEventType(line) == "result";

    /// <summary>
    /// The complete lines from the stream's position, each with the offset after it, without its
    /// <c>\n</c> or a <c>\r</c> before that. Stops at the end of the file as it is when reached.
    /// </summary>
    private static async IAsyncEnumerable<OutputLine> ReadLinesAsync(FileStream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        var position = stream.Position;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newLine = Array.IndexOf(buffer, NewLine, start, read - start);
                if (newLine < 0)
                {
                    line.Write(buffer, start, read - start);
                    break;
                }

                line.Write(buffer, start, newLine - start);
                var end = position + newLine + 1;
                var length = (int)line.Length;
                if (length > 0 && line.GetBuffer()[length - 1] == (byte)'\r') length--;
                yield return new OutputLine(end, Utf8.GetString(line.GetBuffer(), 0, length));
                line.SetLength(0);
                start = newLine + 1;
            }
            position += read;
        }
    }
}
