using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core;

public sealed record InternetProbeBinding(IPAddress Source, int InterfaceIndex);

public interface IInternetProbe
{
    Task<EndpointResult> CheckAsync(InternetProbeBinding binding, CancellationToken ct);
}
