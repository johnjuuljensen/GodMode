using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>Keeps the server's warnings and errors in memory, so a failed lifecycle test can show them.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category.Split('.')[^1]}: {formatter(state, exception)}";
            provider._lines.Enqueue(exception == null ? line : $"{line}\n    {exception.GetType().Name}: {exception.Message}");
        }
    }
}
