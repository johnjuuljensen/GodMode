using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Logging;

/// <summary>
/// Simple file logger that writes to ~/.godmode/logs/godmode-{date}.log.
/// Rolls daily, keeps last 7 days.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _writeLock = new();
    private StreamWriter? _writer;
    private string? _currentDate;

    public FileLoggerProvider(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
        CleanOldLogs();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void WriteEntry(string category, LogLevel level, string message)
    {
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");

        lock (_writeLock)
        {
            if (_currentDate != dateStr)
            {
                _writer?.Dispose();
                var path = Path.Combine(_logDir, $"godmode-{dateStr}.log");
                _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                _currentDate = dateStr;
            }
            var shortLevel = level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            _writer!.WriteLine($"{now:HH:mm:ss.fff} [{shortLevel}] {shortCategory}: {message}");
        }
    }

    private void CleanOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(_logDir, "godmode-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

internal sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception != null) message += $"\n  {exception}";
        provider.WriteEntry(categoryName, logLevel, message);
    }
}
