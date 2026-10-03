namespace NetRoute.Core.Tests;

sealed class FakeSingBoxHost : ISingBoxHost
{
    public bool IsRunning { get; private set; }
    public List<string> Starts { get; } = new();
    public int Stops { get; private set; }
    public Exception? StartThrows { get; set; }
    public Exception? StopThrows { get; set; }
    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (StartThrows is { } ex) throw ex;
        Starts.Add(configJson);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        if (StopThrows is { } ex) throw ex;
        IsRunning = false;
        Exited?.Invoke(0); // contract: raised before StopAsync completes
        return Task.CompletedTask;
    }

    /// Simulates a crash: process gone. Tests then call controller.HandleExitAsync(code).
    public void Crash() => IsRunning = false;

    public void RaiseLine(string line) => LineReceived?.Invoke(line);
}

sealed class FakeSingBoxApi : ISingBoxApi, IDisposable
{
    public bool ConnectionsUnreachable { get; set; }
    public bool SelectResult { get; set; } = true;
    public int Disposals { get; private set; }
    public void Dispose() => Disposals++;

    public List<(string Group, string Outbound)> Selects { get; } = new();
    public List<SingBoxConnection> Connections { get; } = new();

    /// Bytes of connections that already closed: they are in the totals but no longer in Connections.
    public long ClosedUpload { get; set; }
    public long ClosedDownload { get; set; }

    public Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)
    {
        Selects.Add((group, outbound));
        return Task.FromResult(SelectResult);
    }

    public Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default)
    {
        if (ConnectionsUnreachable) return Task.FromResult<ConnectionsSnapshot?>(null);
        var open = Connections.ToList();
        return Task.FromResult<ConnectionsSnapshot?>(new ConnectionsSnapshot(
            open, open.Sum(c => c.Upload) + ClosedUpload, open.Sum(c => c.Download) + ClosedDownload));
    }
}

sealed class FakeAdapterCounters : IAdapterCounters
{
    public long? Bytes { get; set; }
    public Exception? Throws { get; set; }
    public List<string> Asked { get; } = new();

    public long? TotalBytes(string adapterName)
    {
        Asked.Add(adapterName);
        if (Throws is { } ex) throw ex;
        return Bytes;
    }
}
