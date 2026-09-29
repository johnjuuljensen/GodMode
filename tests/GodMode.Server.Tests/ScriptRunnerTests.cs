using System.Collections.Concurrent;
using System.Diagnostics;
using GodMode.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// A run's log is written from its script's stdout and stderr, each read on a thread of its own, and
/// from the run itself (#332): every line reaches it whole, and the run neither fails over it nor
/// closes it before the lines the script wrote last.
/// </summary>
public sealed class ScriptRunnerTests : IDisposable
{
    private const int Lines = 2000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "godmode-script-runner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScriptRunner _runner = new(NullLogger<ScriptRunner>.Instance, new ConfigurationBuilder().Build());

    public ScriptRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Writes <see cref="Lines"/> lines to stdout and as many to stderr, interleaved, as fast as it can.</summary>
    private const string FloodScript = """
        $ErrorActionPreference = 'Stop'
        $out = [Console]::Out
        $err = [Console]::Error
        for ($i = 0; $i -lt 2000; $i++) { $out.WriteLine("out $i"); $err.WriteLine("err $i") }
        """;

    [Fact]
    public async Task AScriptFloodingStdoutAndStderr_IsLoggedWhole_AndTheRunSucceeds()
    {
        File.WriteAllText(Path.Combine(_dir, "flood.ps1"), FloodScript);

        for (var run = 0; run < 5; run++)
        {
            var logPath = Path.Combine(_dir, "logs", $"run-{run}.log");
            var progress = new ConcurrentQueue<string>();

            await _runner.RunAsync(["flood.ps1"], _dir, _dir, new(), line => { progress.Enqueue(line); return Task.CompletedTask; }, logPath)
                .WaitAsync(TimeSpan.FromSeconds(60));

            var log = File.ReadAllLines(logPath);
            Assert.Equal(Enumerable.Range(0, Lines).Select(i => $"[stdout] out {i}"), log.Where(line => line.StartsWith("[stdout]")));
            Assert.Equal(Enumerable.Range(0, Lines).Select(i => $"[stderr] err {i}"), log.Where(line => line.StartsWith("[stderr]")));
            // The exit is logged after every line the script wrote, and nothing else is in between
            var exit = Array.FindIndex(log, line => line.EndsWith("] Exit code: 0"));
            Assert.True(exit == 2 + 2 * Lines, $"run {run}: the exit is line {exit} of the log, not the one after the script's lines");
            Assert.EndsWith($"] Completed: {Path.Combine(_dir, "flood.ps1")}", log[^1]);
            Assert.Equal(Enumerable.Range(0, Lines).Select(i => $"out {i}"), progress.Where(line => line.StartsWith("out ")));
        }
    }

    /// <summary>
    /// A line the script's output carries after its exit, here from a child it left to write it a
    /// second later, is logged and reported before the exit is, and before the run returns.
    /// </summary>
    [Fact]
    public async Task ALineWrittenAfterTheExit_IsLoggedAndReported_BeforeTheRunEnds()
    {
        File.WriteAllText(Path.Combine(_dir, "late.ps1"), """
            $ErrorActionPreference = 'Stop'
            if ($IsWindows) { Start-Process cmd -ArgumentList '/c', 'ping -n 2 127.0.0.1 >nul & echo late' -NoNewWindow }
            else { Start-Process sh -ArgumentList '-c', 'sleep 1; echo late' -NoNewWindow }
            'early'
            """);
        var logPath = Path.Combine(_dir, "logs", "late.log");
        var progress = new ConcurrentQueue<string>();

        await _runner.RunAsync(["late.ps1"], _dir, _dir, new(), line => { progress.Enqueue(line); return Task.CompletedTask; }, logPath)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains("late", progress.Select(line => line.Trim()));
        var log = File.ReadAllLines(logPath);
        var late = Array.FindIndex(log, line => line.Trim() == "[stdout] late");
        var exit = Array.FindIndex(log, line => line.EndsWith("] Exit code: 0"));
        Assert.True(late >= 0 && late < exit, $"the late line is line {late} of the log, and the exit {exit}:\n{string.Join("\n", log)}");
    }

    /// <summary>
    /// A child the script leaves running in the background holds its output open: the run waits for
    /// it no longer than <see cref="ScriptRunner.DrainAfterExit"/>.
    /// </summary>
    [Fact]
    public async Task AChildHoldingTheScriptsOutput_DoesNotHoldTheRun()
    {
        var pidFile = Path.Combine(_dir, "child.pid");
        File.WriteAllText(Path.Combine(_dir, "background.ps1"), $$"""
            $ErrorActionPreference = 'Stop'
            $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -NoNewWindow -PassThru
            Set-Content -Path '{{pidFile}}' -Value $child.Id
            'started'
            """);
        var logPath = Path.Combine(_dir, "logs", "background.log");
        try
        {
            var took = Stopwatch.StartNew();
            await _runner.RunAsync(["background.ps1"], _dir, _dir, new(), _ => Task.CompletedTask, logPath)
                .WaitAsync(TimeSpan.FromSeconds(60));
            took.Stop();

            // The child sleeps a minute; the run takes pwsh's start and the drain's two seconds
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(20), $"the run waited {took.Elapsed} for the child it left running");
            Assert.True(Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim())) is { HasExited: false }, "the child had exited: it held nothing");
            var log = File.ReadAllLines(logPath);
            Assert.Contains("[stdout] started", log);
            Assert.Contains(log, line => line.EndsWith("] Exit code: 0"));
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try { Process.GetProcessById(pid).Kill(); }
                catch { /* gone */ }
            }
        }
    }
}
