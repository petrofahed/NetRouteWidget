using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

public static class FreePort
{
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
