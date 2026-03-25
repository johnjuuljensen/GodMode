using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace SignalR.Proxy;

/// <summary>
/// Creates a HubConnection backed by a pipe pair that we can tee relay data into.
/// S→C messages written to TeeWriter are dispatched by the HubConnection (for Register&lt;T&gt;).
/// </summary>
public sealed class TeeConnection : IConnectionFactory, IAsyncDisposable
{
    private const char RecordSeparator = '\x1e';
    private readonly Pipe _incoming = new(); // we write, HubConnection reads (S→C tee)
    private readonly Pipe _outgoing = new(); // HubConnection writes, we read (proxy→S)

    /// <summary>Write tee'd S→C bytes here. HubConnection will process them.</summary>
    public PipeWriter TeeWriter => _incoming.Writer;

    /// <summary>Read proxy's own outbound messages (pings, invocations) here.</summary>
    public PipeReader ProxyOutput => _outgoing.Reader;

    public ValueTask<ConnectionContext> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        var context = new PipeConnectionContext(
            Guid.NewGuid().ToString("N")[..8],
            new DuplexPipe(_incoming.Reader, _outgoing.Writer));
        return new ValueTask<ConnectionContext>(context);
    }

    public async ValueTask DisposeAsync()
    {
        await _incoming.Writer.CompleteAsync();
        await _outgoing.Writer.CompleteAsync();
    }

    public static HubConnection CreateHubConnection(TeeConnection tee)
    {
        var builder = new HubConnectionBuilder();
        builder.Services.AddSingleton<IConnectionFactory>(tee);
        builder.Services.AddSingleton<EndPoint>(new DnsEndPoint("tee", 0));
        builder.AddJsonProtocol();
        var connection = builder.Build();
        connection.ServerTimeout = TimeSpan.FromDays(1);
        connection.KeepAliveInterval = TimeSpan.FromDays(1);
        return connection;
    }

    public static async Task<(TeeConnection Tee, HubConnection Hub)> CreateHubConnectionAsync()
    {
        var tee = new TeeConnection();
        var hubConnection = CreateHubConnection(tee);

        // Start HubConnection — it sends handshake through pipe, we fake the response
        var startTask = hubConnection.StartAsync();
        // Read the handshake request from the pipe output and discard it
        var handshakeOut = await tee.ProxyOutput.ReadAsync();
        tee.ProxyOutput.AdvanceTo(handshakeOut.Buffer.End);
        // Write a fake handshake response into the pipe input
        var fakeResponse = Encoding.UTF8.GetBytes("{}" + RecordSeparator);
        await tee.TeeWriter.WriteAsync(new ReadOnlyMemory<byte>(fakeResponse));
        await tee.TeeWriter.FlushAsync();
        await startTask;

        return (tee, hubConnection);
    }

    private sealed class PipeConnectionContext(string id, IDuplexPipe transport) : ConnectionContext
    {
        public override string ConnectionId { get; set; } = id;
        public override IDuplexPipe Transport { get; set; } = transport;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}
