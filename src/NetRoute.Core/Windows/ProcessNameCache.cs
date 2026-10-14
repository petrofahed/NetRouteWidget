using System.Collections.Concurrent;

namespace NetRoute.Core.Windows;

/// Process id -> image file name ("chrome.exe"), remembered for a while: the trace sees thousands of packets per process and
/// resolving a name is a system call. Ids are reused, so an entry expires; an id that cannot be resolved is remembered as unknown.
public sealed class ProcessNameCache(Func<int, string?> resolve, TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    const int MaxEntries = 4096;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<int, (string? Name, DateTimeOffset At)> _names = new();

    public string? NameOf(int pid)
    {
        var now = _time.GetUtcNow();
        if (_names.TryGetValue(pid, out var hit) && now - hit.At < Lifetime) return hit.Name;

        string? name;
        try
        {
            name = resolve(pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            name = null; // a protected or vanished process: its traffic counts under "Other"
        }
        if (_names.Count >= MaxEntries) _names.Clear(); // a bound, not a policy: ids come and go
        _names[pid] = (name, now);
        return name;
    }
}
