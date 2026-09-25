using System.Runtime.CompilerServices;
using System.Text;
using GodMode.ProjectFiles;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// A project's <c>output.jsonl</c>: one JSON line per line of claude's output, each ended by
/// <c>\n</c>, appended only by the project's consumer. An offset is a byte position in the file;
/// a line's offset is the position just after its <c>\n</c>, which is where a client that has seen
/// it resumes. Files written before offsets existed end their lines with <c>\r\n</c>; they read
/// the same, with each offset still after the <c>\n</c>.
/// <para>
/// An offset is into one generation of the file, named in <c>output-generation</c> beside it: a
/// project created, or deleted and created again with the same ID, starts a new one (see <see cref="StartGeneration"/>).
/// </para>
/// </summary>
public static class OutputLog
{
    /// <summary>At most this many lines go in one replayed batch.</summary>
    public const int MaxBatchLines = 1000;

    /// <summary>A batch is closed once its lines reach this many bytes (a single longer line is sent on its own).</summary>
    public const int MaxBatchBytes = 1 << 20;

    /// <summary>How much a backward read takes at a time.</summary>
    private const int BlockSize = 64 * 1024;

    private const byte NewLine = (byte)'\n';
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string PathOf(string projectPath) => Path.Combine(projectPath, ".godmode", "output.jsonl");

    public static string GenerationPathOf(string projectPath) => Path.Combine(projectPath, ".godmode", "output-generation");

    /// <summary>
    /// Starts a new generation of the project's output, replacing any it had, and returns it. The
    /// project's <c>.godmode</c> is set up with one, so an ID that is deleted and created again gets
    /// a new one: a client's offset from before is then not taken for an offset in the new file.
    /// </summary>
    public static string StartGeneration(string projectPath)
    {
        var generation = NewGenerationId();
        AtomicFile.WriteAllText(GenerationPathOf(projectPath), generation);
        return generation;
    }

    /// <summary>
    /// The generation of the project's output. A project folder from before generations has none:
    /// its output is a new generation, written now, so every later read agrees on it. When two
    /// first reads race, the one whose file lands first is the generation. A project with no
    /// <c>.godmode</c> (a create that failed before its script made the folder) has no output: any
    /// generation is right for that, and none is written where the project has no folder.
    /// </summary>
    public static async Task<string> GenerationAsync(string projectPath, CancellationToken ct = default)
    {
        var path = GenerationPathOf(projectPath);
        try
        {
            if (await ReadGenerationAsync(path, ct) is { } generation) return generation;

            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, NewGenerationId(), Utf8, ct);
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path)) { /* another read wrote it first */ }
            finally { File.Delete(temp); }

            // A file there but blank (edited by hand) names no generation: this one starts one
            return await ReadGenerationAsync(path, ct) ?? StartGeneration(projectPath);
        }
        catch (DirectoryNotFoundException)
        {
            return NewGenerationId();
        }
    }

    private static string NewGenerationId() => Guid.NewGuid().ToString("N");

    /// <summary>How often a read of the generation is tried while something else holds the file.</summary>
    private const int ReadAttempts = 20;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The generation in the file, or null when there is none. Shared for delete, so a rename onto
    /// the file (a first read's, or <see cref="StartGeneration"/>'s) is never refused for it, and
    /// retried for a moment while one is under way: on Windows the renamed file is held with delete
    /// access until the rename is done, as it can be by a virus scanner.
    /// </summary>
    private static async Task<string?> ReadGenerationAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8);
                var text = (await reader.ReadToEndAsync(ct)).Trim();
                return text.Length > 0 ? text : null;
            }
            catch (FileNotFoundException) { return null; }
            // A missing .godmode is not something holding the file: the caller has no output to name
            catch (IOException ex) when (ex is not DirectoryNotFoundException && attempt < ReadAttempts) { await Task.Delay(ReadRetryDelay, ct); }
        }
    }

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
    /// <item>Past the end: 0. The offset is not from this file (it was cut or replaced), so all of it is replayed.
    /// An offset from another generation is not from it either; the subscription replaces it with 0 before asking.</item>
    /// <item>Negative, <c>-N</c>: the last N turns. That is after the Nth-last <c>result</c> line, not counting one that ends the file, or 0 if there are fewer.
    /// The file is read from its end, back only as far as those turns go.</item>
    /// </list>
    /// </summary>
    public static async Task<long> StartAsync(string projectPath, long fromOffset, CancellationToken ct = default)
    {
        var path = PathOf(projectPath);
        if (fromOffset == 0 || !File.Exists(path)) return 0;

        await using var stream = OpenRead(path);
        return await StartAsync(stream, fromOffset, ct);
    }

    /// <summary><see cref="StartAsync(string, long, CancellationToken)"/> in the file open as <paramref name="stream"/>.</summary>
    internal static async Task<long> StartAsync(Stream stream, long fromOffset, CancellationToken ct = default)
    {
        if (fromOffset == 0) return 0;
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
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (_stream.Length == 0) return;

            // A write cut short left a line without its \n: end it, so the next line starts on a boundary
            _stream.Position = _stream.Length - 1;
            if (_stream.ReadByte() != NewLine)
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

    /// <summary>The offset after the last <c>\n</c> in the stream, or 0.</summary>
    private static long LastLineEnd(Stream stream)
    {
        var buffer = new byte[BlockSize];
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

    private static async Task<long> TailStartAsync(Stream stream, long turns, CancellationToken ct)
    {
        // From the end: a result line that ends the file ends a turn with nothing after it, so it is
        // not counted, and the Nth-last of the others is where the last N turns start
        var lastEnd = LastLineEnd(stream);
        var results = 0L;
        await foreach (var line in ReadLinesBackwardAsync(stream, lastEnd, ct))
            if (line.Offset != lastEnd && IsResult(line.RawJson) && ++results == turns)
                return line.Offset;
        return 0;
    }

    private static bool IsResult(string line) =>
        line.Contains("\"result\"", StringComparison.Ordinal) && ProjectLifecycle.ExtractEventType(line) == "result";

    /// <summary>
    /// The complete lines that end at or before <paramref name="end"/> (a line boundary), last first,
    /// each as <see cref="ReadLinesAsync"/> gives it. The stream is read backwards a block at a time,
    /// so only as far back as the caller keeps asking.
    /// </summary>
    private static async IAsyncEnumerable<OutputLine> ReadLinesBackwardAsync(Stream stream, long end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (end <= 0) yield break;
        var buffer = new byte[BlockSize];
        // The line being read ends with the \n at lineEnd - 1; its bytes read so far are in parts, last part first
        var parts = new List<byte[]>();
        var lineEnd = end;
        for (var blockEnd = end - 1; ; )
        {
            var blockStart = Math.Max(0, blockEnd - BlockSize);
            var length = (int)(blockEnd - blockStart);
            stream.Position = blockStart;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct);

            // Each \n in the block, from its end, is where the line being read starts
            for (var textEnd = length; ;)
            {
                var newLine = textEnd > 0 ? Array.LastIndexOf(buffer, NewLine, textEnd - 1, textEnd) : -1;
                parts.Add(buffer[(newLine + 1)..textEnd]);
                if (newLine < 0) break;
                yield return JoinLine(lineEnd, parts);
                parts.Clear();
                lineEnd = blockStart + newLine + 1;
                textEnd = newLine;
            }

            // The file's first line starts at 0
            if (blockStart == 0)
            {
                yield return JoinLine(lineEnd, parts);
                yield break;
            }
            blockEnd = blockStart;
        }
    }

    /// <summary>A line read backwards, from its parts (last first), without a <c>\r</c> before its <c>\n</c>.</summary>
    private static OutputLine JoinLine(long offset, List<byte[]> parts)
    {
        var bytes = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            parts[i].CopyTo(bytes, at);
            at += parts[i].Length;
        }
        var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
        return new OutputLine(offset, Utf8.GetString(bytes, 0, length));
    }

    /// <summary>
    /// The complete lines from the stream's position, each with the offset after it, without its
    /// <c>\n</c> or a <c>\r</c> before that. Stops at the end of the file as it is when reached.
    /// </summary>
    private static async IAsyncEnumerable<OutputLine> ReadLinesAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[BlockSize];
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
