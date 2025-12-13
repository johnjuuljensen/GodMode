using System.Text;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Thread-safe writer for JSONL (JSON Lines) files.
/// </summary>
public sealed class JsonlWriter : IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new JSONL writer for the specified file.
    /// </summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    public JsonlWriter(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Appends an output event to the JSONL file in a thread-safe manner.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when writer is disposed.</exception>
    /// <exception cref="IOException">Thrown when file I/O fails.</exception>
    public async Task AppendAsync(OutputEvent evt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(evt, ProjectJsonContext.Default.OutputEvent);
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Appends an output event to the JSONL file synchronously in a thread-safe manner.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    /// <exception cref="ObjectDisposedException">Thrown when writer is disposed.</exception>
    /// <exception cref="IOException">Thrown when file I/O fails.</exception>
    public void Append(OutputEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _writeLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(evt, ProjectJsonContext.Default.OutputEvent);
            File.AppendAllText(_filePath, json + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Appends multiple output events to the JSONL file in a thread-safe manner.
    /// </summary>
    /// <param name="events">The events to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when writer is disposed.</exception>
    /// <exception cref="IOException">Thrown when file I/O fails.</exception>
    public async Task AppendBatchAsync(IEnumerable<OutputEvent> events, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var sb = new StringBuilder();
            foreach (var evt in events)
            {
                var json = JsonSerializer.Serialize(evt, ProjectJsonContext.Default.OutputEvent);
                sb.AppendLine(json);
            }

            if (sb.Length > 0)
            {
                await File.AppendAllTextAsync(_filePath, sb.ToString(), Encoding.UTF8, cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets the current file size in bytes.
    /// </summary>
    /// <returns>File size in bytes, or 0 if file doesn't exist.</returns>
    public long GetCurrentOffset()
    {
        return File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writeLock.Dispose();
    }
}
