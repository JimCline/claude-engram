using System.Net;
using System.Net.Sockets;

namespace Engram.EndToEnd.Tests;

internal static class FreeTcpPort
{
    public static int Next()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
