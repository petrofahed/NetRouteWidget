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

    public Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)
    {
        Selects.Add((group, outbound));
        return Task.FromResult(SelectResult);
    }

    public Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SingBoxConnection>?>(ConnectionsUnreachable ? null : Connections.ToList());
}
