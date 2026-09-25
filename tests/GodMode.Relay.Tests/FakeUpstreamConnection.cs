using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace GodMode.Relay.Tests;

/// <summary>
/// An upstream server the test plays byte by byte. As the relay's <see cref="IConnectionFactory"/> it hands over one
/// connection whose server side the test writes; registered by factory, it is owned (and disposed) by the relay's
/// service provider.
/// </summary>
internal sealed class FakeUpstreamConnection : IConnectionFactory, IAsyncDisposable
{
    private readonly Pipe _toRelay = new();
    private readonly Pipe _fromRelay = new();

    /// <summary>Thrown by <see cref="ConnectAsync"/>, as a failed negotiate throws.</summary>
    public Exception? Refuse { get; init; }

    /// <summary>The service provider that owns this factory was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>The relay disposed the connection it was handed.</summary>
    public bool ConnectionDisposed { get; private set; }

    /// <summary>The server writes <paramref name="text"/>, then, with <paramref name="thenHangUp"/>, ends the stream.</summary>
    public async Task ServerSendsAsync(string text, bool thenHangUp = false)
    {
        await _toRelay.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        if (thenHangUp) await _toRelay.Writer.CompleteAsync();
    }

    public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        Refuse != null
            ? ValueTask.FromException<ConnectionContext>(Refuse)
            : ValueTask.FromResult<ConnectionContext>(new Connection(this));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class Connection(FakeUpstreamConnection owner) : DefaultConnectionContext(
        Guid.NewGuid().ToString("N"),
        new Duplex(owner._toRelay.Reader, owner._fromRelay.Writer),
        new Duplex(owner._fromRelay.Reader, owner._toRelay.Writer))
    {
        public override ValueTask DisposeAsync()
        {
            owner.ConnectionDisposed = true;
            return base.DisposeAsync();
        }
    }

    private sealed record Duplex(PipeReader Input, PipeWriter Output) : IDuplexPipe;
}
