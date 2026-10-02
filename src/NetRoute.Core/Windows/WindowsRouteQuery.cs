using System.Net;

namespace NetRoute.Core.Windows;

/// Asks Windows which interface it would use for a destination, honouring interface metrics.
public sealed class WindowsRouteQuery : IRouteQuery
{
    public int? GetBestInterfaceIndex(IPAddress destination)
    {
        var socketAddress = new IPEndPoint(destination, 0).Serialize();
        var buffer = new byte[socketAddress.Size];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = socketAddress[i];

        return IpHelperNative.GetBestInterfaceEx(buffer, out var index) == IpHelperNative.NO_ERROR ? (int)index : null;
    }
}
