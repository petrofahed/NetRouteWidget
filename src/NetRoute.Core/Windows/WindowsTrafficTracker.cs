using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetRoute.Core.Windows;

/// Counts bytes per process and exit from the Windows kernel network trace (ETW), so usage can be recorded while Smart
/// routing is off. Needs administrator rights. A named user-mode session, so it never fights over the single "NT Kernel
/// Logger". The trace thread only adds to a table under a short lock; Drain swaps the table out.
public sealed class WindowsTrafficTracker : ITrafficSource
{
    public const string SessionName = "NetRouteWidget-Traffic";
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    const ulong Ipv4AndIpv6Keywords = 0x10 | 0x20;

    readonly ProcessNameCache _names;
    readonly TimeProvider _time;
    readonly Func<string, IReadOnlyList<IPAddress>> _addressesOf;
    readonly object _gate = new();
    Dictionary<(int Pid, RouteExit Exit), (long Up, long Down)> _table = new();
    volatile TrafficClassifier _classifier = TrafficClassifier.None;
    TraceEventSession? _session;
    volatile bool _running;
    bool _disposed;
    string? _lastError;
    DateTimeOffset _nextTry;

    public WindowsTrafficTracker(
        ProcessNameCache? names = null, TimeProvider? time = null,
        Func<string, IReadOnlyList<IPAddress>>? addressesOf = null)
    {
        _names = names ?? new ProcessNameCache(ProcessImage.NameOf);
        _time = time ?? TimeProvider.System;
        _addressesOf = addressesOf ?? AdapterAddresses.FromWindows;
    }

    public bool IsRunning => _running;

    public string? EnsureRunning()
    {
        if (_running) return null;
        if (_disposed) return "stopped";
        var now = _time.GetUtcNow();
        if (now < _nextTry) return _lastError ?? "waiting to retry";
        try
        {
            StopSession(); // tidy a session whose thread ended
            var session = new TraceEventSession(SessionName) { StopOnDispose = true }; // replaces a leftover session of the same name
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, Ipv4AndIpv6Keywords);
            session.Source.Dynamic.All += OnEvent;
            _session = session;
            _running = true;
            new Thread(() => Process(session)) { IsBackground = true, Name = "NetRoute traffic trace" }.Start();
            _lastError = null;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _nextTry = now + RetryDelay;
            StopSession();
            return _lastError;
        }
    }

    public void SetAdapters(AdapterInfo? phone, AdapterInfo? lan) =>
        _classifier = new TrafficClassifier(AdapterAddresses.Of(phone, _addressesOf), AdapterAddresses.Of(lan, _addressesOf));

    public IReadOnlyList<MeasuredTraffic> Drain()
    {
        Dictionary<(int Pid, RouteExit Exit), (long Up, long Down)> taken;
        lock (_gate)
        {
            taken = _table;
            _table = new();
        }
        var merged = new Dictionary<(string? Name, RouteExit Exit), Traffic>();
        foreach (var ((pid, exit), (up, down)) in taken)
        {
            var key = (_names.NameOf(pid), exit);
            var traffic = new Traffic(up, down);
            merged[key] = merged.TryGetValue(key, out var existing) ? existing + traffic : traffic;
        }
        return merged.Select(m => new MeasuredTraffic(m.Key.Name, m.Key.Exit, m.Value)).ToList();
    }

    public void Dispose()
    {
        _disposed = true;
        StopSession();
    }

    void OnEvent(TraceEvent data)
    {
        var id = (int)data.ID;
        if (!KernelNetworkEvents.IsData(id)) return;
        if (EtwPayload.ToAddress(data.PayloadByName("saddr")) is not { } a || EtwPayload.ToAddress(data.PayloadByName("daddr")) is not { } b) return;
        var size = EtwPayload.ToLong(data.PayloadByName("size"));
        var pid = (int)EtwPayload.ToLong(data.PayloadByName("PID")); // the owning process, not the one that logged the event
        Record(id, a, b, size, pid);
    }

    /// Adds one packet's bytes to the table when it is data on one of our adapters to a real internet address.
    internal void Record(int eventId, IPAddress a, IPAddress b, long size, int pid)
    {
        if (!KernelNetworkEvents.IsData(eventId) || size <= 0) return;
        if (_classifier.ExitOf(a, b) is not { } exit) return;

        _names.NameOf(pid); // resolved now, while the process most likely still exists
        var send = KernelNetworkEvents.IsSend(eventId);
        lock (_gate)
        {
            _table.TryGetValue((pid, exit), out var total);
            _table[(pid, exit)] = send ? (total.Up + size, total.Down) : (total.Up, total.Down + size);
        }
    }

    void Process(TraceEventSession session)
    {
        try
        {
            session.Source.Process(); // blocks until the session is disposed or stopped
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_session, session)) _running = false; // a newer session must not be marked stopped by an old thread
        }
    }

    void StopSession()
    {
        var session = _session;
        _session = null;
        _running = false;
        try
        {
            session?.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // already gone
        }
    }
}
