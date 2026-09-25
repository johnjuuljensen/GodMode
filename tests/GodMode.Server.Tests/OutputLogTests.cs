using System.Text;
using Xunit.Abstractions;
using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// Offsets in output.jsonl are bytes, and every offset the server hands out is a line boundary it
/// can resume from, whatever the file's line endings or content.
/// </summary>
public sealed class OutputLogTests : IDisposable
{
    private readonly string _projectPath = ServerProcess.CreateWorkDir("outputlog");
    private readonly ITestOutputHelper _output;

    public OutputLogTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_projectPath, ".godmode"));
    }

    public void Dispose() => ServerProcess.DeleteWorkDir(_projectPath);

    private string FilePath => OutputLog.PathOf(_projectPath);

    private void WriteFile(string text) => File.WriteAllBytes(FilePath, Encoding.UTF8.GetBytes(text));

    private static long Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private async Task<(long Offset, string Json)[]> ReadAllAsync(long from)
    {
        var lines = new List<(long, string)>();
        await foreach (var batch in OutputLog.ReadBatchesAsync(_projectPath, from))
            lines.AddRange(batch.Select(l => (l.Offset, l.RawJson)));
        return [.. lines];
    }

    [Fact]
    public async Task Writer_ReturnsTheByteOffsetAfterEachLine_ForNonAscii()
    {
        await using (var writer = OutputLog.OpenWriter(_projectPath))
        {
            Assert.Equal(Bytes("{\"a\":\"æø 🚀\"}\n"), await writer.AppendAsync("{\"a\":\"æø 🚀\"}"));
            Assert.Equal(Bytes("{\"a\":\"æø 🚀\"}\n{\"b\":1}\n"), await writer.AppendAsync("{\"b\":1}"));
        }

        Assert.Equal(new[] { (Bytes("{\"a\":\"æø 🚀\"}\n"), "{\"a\":\"æø 🚀\"}"), (Bytes("{\"a\":\"æø 🚀\"}\n{\"b\":1}\n"), "{\"b\":1}") },
            await ReadAllAsync(0));
        Assert.Equal(new FileInfo(FilePath).Length, OutputLog.End(_projectPath));
    }

    [Fact]
    public async Task CrLfFile_FromBeforeOffsets_ReadsWithoutTheCr_AndOffsetsAfterTheLf()
    {
        WriteFile("{\"a\":\"é\"}\r\n{\"b\":2}\r\n");

        var lines = await ReadAllAsync(0);

        Assert.Equal(new[] { (Bytes("{\"a\":\"é\"}\r\n"), "{\"a\":\"é\"}"), (Bytes("{\"a\":\"é\"}\r\n{\"b\":2}\r\n"), "{\"b\":2}") }, lines);
        Assert.Equal(lines[0].Offset, await OutputLog.StartAsync(_projectPath, lines[0].Offset));
        Assert.Equal(new[] { lines[1] }, await ReadAllAsync(lines[0].Offset));
    }

    [Fact]
    public async Task OffsetInsideALine_SnapsForwardToTheNextLine()
    {
        WriteFile("{\"a\":\"ææææ\"}\n{\"b\":2}\n");
        var boundary = Bytes("{\"a\":\"ææææ\"}\n");

        // Inside a multi-byte character, inside the line, and on the \n itself
        foreach (var inside in new[] { 7L, 3L, boundary - 1 })
            Assert.Equal(boundary, await OutputLog.StartAsync(_projectPath, inside));
    }

    [Fact]
    public async Task OffsetPastTheEnd_IsNotFromThisFile_AndStartsAtZero()
    {
        WriteFile("{\"a\":1}\n");

        Assert.Equal(0, await OutputLog.StartAsync(_projectPath, 1000));
    }

    [Fact]
    public async Task LineCutShort_IsNotRead_AndTheWriterEndsItBeforeAppending()
    {
        WriteFile("{\"a\":1}\n{\"cut");

        Assert.Equal(Bytes("{\"a\":1}\n"), OutputLog.End(_projectPath));
        Assert.Equal(new[] { (Bytes("{\"a\":1}\n"), "{\"a\":1}") }, await ReadAllAsync(0));

        await using (var writer = OutputLog.OpenWriter(_projectPath))
            await writer.AppendAsync("{\"b\":2}");

        Assert.Equal(new[] { "{\"a\":1}", "{\"cut", "{\"b\":2}" }, (await ReadAllAsync(0)).Select(l => l.Json));
    }

    [Fact]
    public async Task Replay_IsBatched()
    {
        WriteFile(string.Concat(Enumerable.Range(0, 2500).Select(i => $"{{\"i\":{i}}}\n")));

        var batches = new List<int>();
        await foreach (var batch in OutputLog.ReadBatchesAsync(_projectPath, 0))
            batches.Add(batch.Count);

        Assert.Equal(new[] { OutputLog.MaxBatchLines, OutputLog.MaxBatchLines, 500 }, batches);
    }

    // ── Generations ──

    [Fact]
    public async Task Generation_OfAFolderFromBeforeGenerations_IsWrittenOnTheFirstRead_AndKept()
    {
        Assert.False(File.Exists(OutputLog.GenerationPathOf(_projectPath)));

        var first = await OutputLog.GenerationAsync(_projectPath);

        Assert.Equal(first, File.ReadAllText(OutputLog.GenerationPathOf(_projectPath)));
        Assert.Equal(first, await OutputLog.GenerationAsync(_projectPath));
    }

    [Fact]
    public async Task Generation_FirstReadsAtOnce_AllAgree()
    {
        // On Windows a read racing the first read's rename used to meet a sharing violation
        for (var round = 0; round < 50; round++)
        {
            File.Delete(OutputLog.GenerationPathOf(_projectPath));

            var generations = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => OutputLog.GenerationAsync(_projectPath))));

            Assert.Single(generations.Distinct());
            Assert.Equal(generations[0], File.ReadAllText(OutputLog.GenerationPathOf(_projectPath)));
            Assert.Equal([OutputLog.GenerationPathOf(_projectPath)], Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "output-generation*"));
        }
    }

    [Fact]
    public async Task StartGeneration_ReplacesTheGeneration()
    {
        var before = await OutputLog.GenerationAsync(_projectPath);

        var started = OutputLog.StartGeneration(_projectPath);

        Assert.NotEqual(before, started);
        Assert.Equal(started, await OutputLog.GenerationAsync(_projectPath));
    }

    // ── The last turns (tail mode) ──

    private const string Result = "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\"}";

    private static string Assistant(string text) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

    /// <summary>
    /// Where the last <paramref name="turns"/> turns start, by the definition read forwards through the
    /// whole file: after the Nth-last result line, not counting one that ends the file, else 0.
    /// </summary>
    private static long TailStartByDefinition(byte[] file, long turns)
    {
        var results = new List<long>();
        long lastEnd = 0;
        for (int start = 0, newLine; (newLine = Array.IndexOf(file, (byte)'\n', start)) >= 0; start = newLine + 1)
        {
            lastEnd = newLine + 1;
            var text = Encoding.UTF8.GetString(file, start, newLine - start).TrimEnd('\r');
            if (text.StartsWith("{\"type\":\"result\"")) results.Add(lastEnd);
        }
        var counted = results.Where(end => end != lastEnd).ToArray();
        return counted.Length >= turns ? counted[^(int)turns] : 0;
    }

    public static TheoryData<string, string> TailFiles => new()
    {
        { "ends with a result", string.Concat(Enumerable.Range(0, 4).Select(i => $"{Assistant($"t{i}")}\n{Result}\n")) },
        { "ends inside a turn", $"{Assistant("a")}\n{Result}\n{Assistant("b")}\n{Result}\n{Assistant("c")}\n" },
        { "CRLF", $"{Assistant("a")}\r\n{Result}\r\n{Assistant("b")}\r\n{Result}\r\n{Assistant("c")}\r\n{Result}\r\n" },
        { "a line cut short at the end", $"{Assistant("a")}\n{Result}\n{Assistant("b")}\n{Result}\n{{\"type\":\"res" },
        { "lines longer than a block", $"{Assistant(new string('x', 200_000))}\n{Result}\n{Assistant(new string('é', 70_000))}\n{Result}\n{Assistant("c")}\n{Result}\n" },
        { "a result longer than a block", $"{Assistant("a")}\n{Result[..^1]},\"text\":\"{new string('y', 150_000)}\"}}\n{Assistant("b")}\n{Result}\n{Assistant("c")}\n" },
        { "no result", $"{Assistant("a")}\n{Assistant("b")}\n" },
        { "blank lines", $"\n{Result}\n\n{Assistant("a")}\n\n{Result}\n" },
        { "empty", "" },
    };

    [Theory]
    [MemberData(nameof(TailFiles))]
    public async Task Tail_ReadFromTheEnd_StartsWhereTheDefinitionSays(string file, string text)
    {
        WriteFile(text);
        var bytes = File.ReadAllBytes(FilePath);

        foreach (var turns in new long[] { 1, 2, 3, 5 })
        {
            var expected = TailStartByDefinition(bytes, turns);
            var start = await OutputLog.StartAsync(_projectPath, -turns);
            Assert.True(expected == start, $"{file}, the last {turns} turns: start at {start}, not {expected}");
        }
    }

    [Fact]
    public async Task Tail_OfA50MBFile_ReadsLessThan1MB()
    {
        // 50 MB of 10-line turns of about 1 KB lines, the last line a result
        var turn = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 9).Select(i => $"{Assistant(new string('z', 1000))}\n")) + $"{Result}\n");
        var turnCount = 50 * 1024 * 1024 / turn.Length;
        await using (var writer = File.Create(FilePath))
            for (var i = 0; i < turnCount; i++) writer.Write(turn);
        var length = new FileInfo(FilePath).Length;

        await using var stream = new CountingStream(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, useAsync: true));
        var start = await OutputLog.StartAsync(stream, -2);

        _output.WriteLine($"tail of the last 2 turns of a {length:N0}-byte output.jsonl: read {stream.BytesRead:N0} bytes");
        Assert.Equal(length - 2 * turn.Length, start);
        Assert.True(stream.BytesRead < 1024 * 1024, $"reading the last 2 turns of a {length:N0}-byte file read {stream.BytesRead:N0} bytes");
    }

    /// <summary>A read-only stream over another that counts the bytes read through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        private int Count(int read) { BytesRead += read; return read; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}
