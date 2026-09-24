using System.Text;
using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// Offsets in output.jsonl are bytes, and every offset the server hands out is a line boundary it
/// can resume from, whatever the file's line endings or content.
/// </summary>
public sealed class OutputLogTests : IDisposable
{
    private readonly string _projectPath = ServerProcess.CreateWorkDir("outputlog");

    public OutputLogTests() => Directory.CreateDirectory(Path.Combine(_projectPath, ".godmode"));

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
}
