namespace NetRoute.Core;

/// One live connection as reported by sing-box's Clash API.
public sealed record SingBoxConnection(
    string Id, string? Host, string? ProcessName, IReadOnlyList<string> Chains, long Upload, long Download)
{
    /// Chains are innermost-first, e.g. ["lan", "lan-only"] for traffic a LAN-only rule sent out of the LAN.
    public bool IsLanOnly =>
        Chains.Count >= 2 && Chains[0] == SingBoxConfigBuilder.LanTag && Chains[^1] == SingBoxConfigBuilder.LanOnlyTag;
}

/// Runs sing-box.exe. Exited fires for every exit; for StopAsync-caused exits it fires before StopAsync completes.
public interface ISingBoxHost
{
    bool IsRunning { get; }
    Task StartAsync(string configJson, CancellationToken ct = default);
    Task StopAsync();
    event Action<string>? LineReceived;
    event Action<int>? Exited;
}

/// sing-box's local Clash API.
public interface ISingBoxApi
{
    Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default);

    /// Null when the API cannot be reached.
    Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default);
}
