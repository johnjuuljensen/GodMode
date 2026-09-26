using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// The real <see cref="StatusUpdater"/>, whose next save of a status a test picks can be made to fail
/// as a status.json that cannot be replaced does (<see cref="FailNext"/>).
/// </summary>
internal sealed class FailingSaves(StatusUpdater inner) : IStatusUpdater
{
    private Func<ProjectStatus, bool>? _failNext;
    private int _failed;

    /// <summary>How many saves have failed so far.</summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>The next save of a status that satisfies <paramref name="when"/> (any, by default) fails; the ones after it do not.</summary>
    public void FailNext(Func<ProjectStatus, bool>? when = null) => _failNext = when ?? (_ => true);

    public Task SaveStatusAsync(ProjectInfo project)
    {
        if (_failNext is { } when && when(project.Status) && Interlocked.CompareExchange(ref _failNext, null, when) == when)
        {
            Interlocked.Increment(ref _failed);
            throw new IOException("status.json is being used by another process (a test's)");
        }
        return inner.SaveStatusAsync(project);
    }

    public Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson) =>
        inner.UpdateFromOutputEventAsync(project, outputEvent, rawJson);

    public Task UpdateGitStatusAsync(ProjectInfo project) => inner.UpdateGitStatusAsync(project);
}

/// <summary>
/// A stream between the consumer's writer and output.jsonl whose first flush fails half-way, as a
/// full disk or a dropped network share does: half the pending bytes reach the file, the flush
/// throws, and so does closing it (its buffer can never be written). Every stream after it is plain.
/// </summary>
internal sealed class FailingAppend
{
    private int _armed = 1;

    /// <summary>Set once the failure has happened.</summary>
    public bool Failed { get; private set; }

    /// <summary>For <see cref="ProjectLifecycle.WrapOutput"/>: wraps the first stream opened, passes the rest.</summary>
    public Stream Wrap(Stream file) => Interlocked.Exchange(ref _armed, 0) == 1 ? new Failing(file, this) : file;

    private sealed class Failing(Stream inner, FailingAppend owner) : Stream
    {
        private readonly MemoryStream _pending = new();
        private bool _broken;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position + _pending.Length; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _pending.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
            if (_broken) throw new IOException("There is not enough space on the disk (a test's)");
            // Before anything is appended (the writer ending a cut line when it opens): passed on
            if (_pending.Length == 0 || owner.Failed)
            {
                _pending.WriteTo(inner);
                _pending.SetLength(0);
                inner.Flush();
                return;
            }
            var bytes = _pending.ToArray();
            inner.Write(bytes, 0, bytes.Length / 2);
            inner.Flush();
            _broken = true;
            owner.Failed = true;
            throw new IOException("There is not enough space on the disk (a test's)");
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flush();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
            if (disposing && _broken) throw new IOException("There is not enough space on the disk (a test's)");
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (_broken) throw new IOException("There is not enough space on the disk (a test's)");
        }
    }
}
