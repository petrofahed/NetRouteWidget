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

    /// Data events in a row, with none ever understood, after which the tracker reports a problem instead of claiming to
    /// count: the field names or types of the trace are not what this code expects.
    public const int UnreadableEventLimit = 1000;

    /// The data events are small and a few thousand per second at most: a few MB of buffers are plenty (TraceEvent's default
    /// is 64 MB, which a tray widget should not pin).
    const int BufferSizeMb = 4;
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
    volatile string? _problem; // set from the trace thread while running; read by EnsureRunning
    long _parsed;
    int _unreadable;

    public WindowsTrafficTracker(
        ProcessNameCache? names = null, TimeProvider? time = null,
        Func<string, IReadOnlyList<IPAddress>>? addressesOf = null)
    {
        _names = names ?? new ProcessNameCache(ProcessImage.NameOf);
        _time = time ?? TimeProvider.System;
        _addressesOf = addressesOf ?? AdapterAddresses.FromWindows;
    }

    public bool IsRunning => _running;

    /// Why a running trace delivers nothing usable (events not understood, a callback failure), or null.
    internal string? Problem => _problem;

    public string? EnsureRunning()
    {
        if (_running) return _problem; // running, but a problem means the numbers cannot be trusted: say so
        if (_disposed) return "stopped";
        var now = _time.GetUtcNow();
        if (now < _nextTry) return _lastError ?? "waiting to retry";
        try
        {
            StopSession(); // tidy a session whose thread ended
            var session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = BufferSizeMb }; // replaces a leftover session of the same name
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
        // Never let an exception escape: it would unwind into native ProcessTrace frames, which aborts the whole process.
        try
        {
            var id = (int)data.ID;
            if (!KernelNetworkEvents.IsData(id)) return;
            // PID is the owning process, not the one that logged the event.
            Observe(id, data.PayloadByName("saddr"), data.PayloadByName("daddr"), data.PayloadByName("size"), data.PayloadByName("PID"));
        }
        catch (Exception ex)
        {
            Fail($"kernel network event could not be read: {ex.Message}");
        }
    }

    internal void Fail(string message) => _problem = message;

    /// Reads one data event's raw payload values (any numeric width) and records it. A long run of data events that cannot
    /// be read, with none ever understood, becomes a problem; a readable event clears it.
    internal void Observe(int eventId, object? saddr, object? daddr, object? size, object? pid)
    {
        if (!KernelNetworkEvents.IsData(eventId)) return;
        var a = EtwPayload.ToAddress(saddr);
        var b = EtwPayload.ToAddress(daddr);
        var bytes = EtwPayload.ToLong(size);
        if (a is null || b is null || bytes <= 0)
        {
            if (_parsed == 0 && ++_unreadable >= UnreadableEventLimit)
                _problem = "kernel network events could not be understood (unexpected fields); not counting";
            return;
        }
        _parsed++;
        _unreadable = 0;
        _problem = null;
        Record(eventId, a, b, bytes, (int)EtwPayload.ToLong(pid));
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
        string? error = null;
        try
        {
            session.Source.Process(); // blocks until the session is disposed or stopped
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
        finally
        {
            TraceEnded(session, error);
        }
    }

    /// The trace thread ended. When it ended on its own (another tool stopped the session, the consumer failed) the reason
    /// is kept and the next start waits RetryDelay, so a failing trace is neither restarted every second nor silent.
    /// A thread of a session we already replaced or disposed changes nothing.
    internal void TraceEnded(TraceEventSession? session, string? error)
    {
        if (!ReferenceEquals(_session, session)) return;
        _running = false;
        _lastError = error ?? "the kernel network trace stopped";
        _nextTry = _time.GetUtcNow() + RetryDelay;
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
