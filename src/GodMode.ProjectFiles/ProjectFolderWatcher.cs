using System.Collections.Concurrent;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Watches a project folder's output.jsonl file for changes and provides events for new content.
/// </summary>
public sealed class ProjectFolderWatcher : IDisposable
{
    private readonly ProjectFolder _projectFolder;
    private readonly FileSystemWatcher _fileWatcher;
    private readonly ConcurrentQueue<Action> _eventQueue = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _processingTask;
    private long _currentOffset;
    private bool _disposed;

    /// <summary>
    /// Event raised when new output events are available.
    /// </summary>
    public event EventHandler<OutputEventsReceivedEventArgs>? OutputEventsReceived;

    /// <summary>
    /// Gets the current byte offset being tracked.
    /// </summary>
    public long CurrentOffset => Interlocked.Read(ref _currentOffset);

    /// <summary>
    /// Creates a new watcher for the specified project folder.
    /// </summary>
    /// <param name="projectFolder">The project folder to watch.</param>
    /// <param name="startOffset">Initial byte offset to start reading from (default: 0).</param>
    /// <exception cref="ArgumentNullException">Thrown when projectFolder is null.</exception>
    public ProjectFolderWatcher(ProjectFolder projectFolder, long startOffset = 0)
    {
        _projectFolder = projectFolder ?? throw new ArgumentNullException(nameof(projectFolder));
        _currentOffset = startOffset;

        var directory = Path.GetDirectoryName(projectFolder.OutputFilePath)
            ?? throw new InvalidOperationException("Invalid output file path");

        var fileName = Path.GetFileName(projectFolder.OutputFilePath);

        _fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false
        };

        _fileWatcher.Changed += OnFileChanged;
        _fileWatcher.Created += OnFileChanged;

        // Start background processing task
        _processingTask = Task.Run(ProcessEventQueueAsync, _cancellationTokenSource.Token);
    }

    /// <summary>
    /// Starts watching for file changes.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_fileWatcher.EnableRaisingEvents)
        {
            _fileWatcher.EnableRaisingEvents = true;

            // Process any existing content from current offset
            ProcessOutputChanges();
        }
    }

    /// <summary>
    /// Stops watching for file changes.
    /// </summary>
    public void Stop()
    {
        if (_disposed || !_fileWatcher.EnableRaisingEvents)
            return;

        _fileWatcher.EnableRaisingEvents = false;
    }

    /// <summary>
    /// Resets the offset to the specified position.
    /// </summary>
    /// <param name="offset">New offset position.</param>
    public void ResetOffset(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _currentOffset, offset);
    }

    /// <summary>
    /// Manually triggers a check for new content (useful for polling scenarios).
    /// </summary>
    public void CheckForChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ProcessOutputChanges();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Queue the processing to avoid blocking the FileSystemWatcher thread
        _eventQueue.Enqueue(ProcessOutputChanges);
    }

    private async Task ProcessEventQueueAsync()
    {
        var token = _cancellationTokenSource.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_eventQueue.TryDequeue(out var action))
                {
                    action.Invoke();
                }
                else
                {
                    // Wait a bit before checking again
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue processing
                OnError(ex);
            }
        }
    }

    private void ProcessOutputChanges()
    {
        if (_disposed)
            return;

        try
        {
            var currentOffset = Interlocked.Read(ref _currentOffset);
            var (events, newOffset) = _projectFolder.ReadOutputFrom(currentOffset);

            var eventsList = events.ToList();
            if (eventsList.Count > 0)
            {
                Interlocked.Exchange(ref _currentOffset, newOffset);
                OnOutputEventsReceived(new OutputEventsReceivedEventArgs(eventsList, newOffset));
            }
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    private void OnOutputEventsReceived(OutputEventsReceivedEventArgs e)
    {
        OutputEventsReceived?.Invoke(this, e);
    }

    private void OnError(Exception ex)
    {
        // Raise as error event with empty events list
        var errorEvent = new OutputEvent(
            DateTime.UtcNow,
            OutputEventType.Error,
            $"Watcher error: {ex.Message}",
            new Dictionary<string, object>
            {
                ["exception"] = ex.GetType().Name,
                ["stackTrace"] = ex.StackTrace ?? string.Empty
            }
        );

        OnOutputEventsReceived(new OutputEventsReceivedEventArgs(
            new[] { errorEvent },
            Interlocked.Read(ref _currentOffset)
        ));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Stop();

        _cancellationTokenSource.Cancel();

        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ignore cancellation exceptions
        }

        _fileWatcher.Changed -= OnFileChanged;
        _fileWatcher.Created -= OnFileChanged;
        _fileWatcher.Dispose();
        _cancellationTokenSource.Dispose();
    }
}

/// <summary>
/// Event arguments for output events received.
/// </summary>
public sealed class OutputEventsReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the list of new output events.
    /// </summary>
    public IReadOnlyList<OutputEvent> Events { get; }

    /// <summary>
    /// Gets the new byte offset after reading these events.
    /// </summary>
    public long NewOffset { get; }

    /// <summary>
    /// Creates a new instance of OutputEventsReceivedEventArgs.
    /// </summary>
    /// <param name="events">The list of output events.</param>
    /// <param name="newOffset">The new byte offset.</param>
    public OutputEventsReceivedEventArgs(IReadOnlyList<OutputEvent> events, long newOffset)
    {
        Events = events ?? throw new ArgumentNullException(nameof(events));
        NewOffset = newOffset;
    }
}
