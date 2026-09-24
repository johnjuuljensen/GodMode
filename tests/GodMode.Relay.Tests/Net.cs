using System.Net;
using System.Net.Sockets;

namespace GodMode.Relay.Tests;

internal static class Net
{
    /// <summary>A loopback URL nothing listens on.</summary>
    public static string UnreachableUrl()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return $"http://127.0.0.1:{port}";
    }

    /// <summary>A loopback listener that accepts connections and never answers.</summary>
    public static (string Url, IDisposable Listener) BlackHole()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ($"http://127.0.0.1:{((IPEndPoint)tcp.LocalEndpoint).Port}", new Stopper(tcp));
    }

    private sealed class Stopper(TcpListener tcp) : IDisposable
    {
        public void Dispose() => tcp.Stop();
    }
}
