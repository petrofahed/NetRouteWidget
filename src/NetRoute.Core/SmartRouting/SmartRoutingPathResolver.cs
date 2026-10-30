namespace NetRoute.Core;

public static class SmartRoutingPathResolver
{
    public static InternetPath? Resolve(bool smartActive, ConnectionId appliedExit, int? queriedIndex, int? tunIndex) =>
        smartActive && tunIndex is not null && queriedIndex == tunIndex ? Connections.PathOf(appliedExit) : null;
}
