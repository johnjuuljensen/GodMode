using System.Diagnostics;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Every event, one line each, to a file the user attaches to the issue and to the window: the wall clock, the time
/// since the app started, and the time since the last reference (a mic open or close, a tone, a pause), so a switch's
/// timing reads straight off the log. Thread-safe: hooks, Core Audio and WinRT call it from their own threads.
/// </summary>
public sealed class SpikeLog : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _lock = new();
    private readonly StreamWriter _file;
    private TimeSpan _reference;
    private string _referenceName = "start";
    private bool _disposed;

    public SpikeLog()
    {
        Directory.CreateDirectory(Folder);
        Path = System.IO.Path.Combine(Folder, $"headset-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(Path, append: false) { AutoFlush = true };
    }

    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodMode.HeadsetSpike", "logs");

    public string Path { get; }

    /// <summary>The window's view of the log; called on the logging thread.</summary>
    public event Action<string>? Written;

    /// <summary>Now, on the clock every edge and gesture uses.</summary>
    public TimeSpan Now => _clock.Elapsed;

    /// <summary>The time since the last reference was set, for a state line.</summary>
    public (string Name, TimeSpan Since) SinceReference
    {
        get
        {
            lock (_lock) return (_referenceName, Now - _reference);
        }
    }

    /// <summary>Starts a new reference: later lines say how long after it they came.</summary>
    public void Reference(string name, string? detail = null)
    {
        lock (_lock)
        {
            _reference = Now;
            _referenceName = name;
        }
        Write("REF", detail is null ? name : $"{name}: {detail}");
    }

    public void Write(string source, string message)
    {
        string line;
        lock (_lock)
        {
            if (_disposed) return;
            var now = Now;
            line = $"{DateTime.Now:HH:mm:ss.fff} t={now.TotalMilliseconds,9:F0}ms {_referenceName}+{(now - _reference).TotalMilliseconds,7:F0}ms [{source,-6}] {message}";
            _file.WriteLine(line);
        }
        Written?.Invoke(line);
    }

    public void Error(string source, string what, Exception ex) => Write(source, $"{what} failed: {ex.GetType().Name}: {ex.Message}");

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _file.Dispose();
        }
    }
}
