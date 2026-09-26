using System.Collections.Concurrent;
using System.Diagnostics;
using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// A child's output is read on a thread of its own: reading a child's pipe from the thread pool
/// holds a pool thread for as long as the child lives on Windows, and many at once starve the pool.
/// </summary>
public class ChildOutputTests
{
    [Fact]
    public async Task LinesArriveInOrder_ThenNull_OffTheThreadPool()
    {
        using var child = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "'one'; 'two'; 'three'"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var lines = new ConcurrentQueue<(string? Line, bool OnThePool)>();

        await ChildOutput.ReadLinesAsync(child.StandardOutput, line => lines.Enqueue((line, Thread.CurrentThread.IsThreadPoolThread)), "test stdout")
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["one", "two", "three", null], lines.Select(l => l.Line));
        Assert.All(lines, l => Assert.False(l.OnThePool, "a line was read on a thread-pool thread"));
    }

    [Fact]
    public async Task ADisposedChild_EndsTheRead()
    {
        var child = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var lines = new ConcurrentQueue<string?>();
        var read = ChildOutput.ReadLinesAsync(child.StandardOutput, lines.Enqueue, "test stdout");

        child.Kill(entireProcessTree: true);
        child.Dispose();

        await read.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([null], lines);
    }
}
