using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Keeps the server's warnings and errors in memory, so a failed lifecycle test can show them, and
/// every line of any level apart (<see cref="AllLines"/>), for a test of what is logged at all.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentQueue<string> _all = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public IReadOnlyCollection<string> AllLines => _all;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category.Split('.')[^1]}: {formatter(state, exception)}";
            line = exception == null ? line : $"{line}\n    {exception.GetType().Name}: {exception.Message}";
            provider._all.Enqueue(line);
            if (logLevel >= LogLevel.Warning) provider._lines.Enqueue(line);
        }
    }
}
