namespace GodMode.Server.Services;

/// <summary>
/// Reads a child process's redirected stdout or stderr line by line, on a thread of its own.
/// <see cref="System.Diagnostics.Process.BeginOutputReadLine"/> and the async reads of
/// <see cref="System.Diagnostics.Process.StandardOutput"/> hold a thread-pool thread for as long as
/// the child lives on Windows, where its pipes are not opened for overlapped I/O: each read is a
/// blocking read queued to the pool. A server with many sessions and scripts at once then starves
/// the pool, and everything else it does (hub calls, a turn's lines, a stop) waits seconds for a
/// thread. A thread per stream is held by the stream alone.
/// </summary>
public static class ChildOutput
{
    /// <summary>
    /// Starts reading <paramref name="reader"/> to its end: <paramref name="onLine"/> gets each line,
    /// then null, as a <see cref="System.Diagnostics.DataReceivedEventHandler"/> does. The task
    /// completes once it has had the null.
    /// </summary>
    /// <param name="name">The thread's name, for a dump.</param>
    public static Task ReadLinesAsync(TextReader reader, Action<string?> onLine, string name)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                try
                {
                    while (reader.ReadLine() is { } line) onLine(line);
                }
                // The process was disposed, or its pipe broke: that is its end
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                onLine(null);
            }
            finally { read.TrySetResult(); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return read.Task;
    }
}
