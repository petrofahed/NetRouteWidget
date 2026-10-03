namespace NetRoute.Core;

/// Allows at most <c>maxPerWindow</c> lines per fixed window. Lines over the limit are dropped and counted;
/// the first allowed line of the next window reports how many were dropped. Thread-safe.
public sealed class LogRateLimiter(int maxPerWindow, TimeSpan window, TimeProvider? time = null)
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly object _gate = new();
    long _windowStart = (time ?? TimeProvider.System).GetTimestamp();
    int _count;
    int _suppressed;

    public bool TryAllow(out int suppressedInPreviousWindow)
    {
        lock (_gate)
        {
            suppressedInPreviousWindow = 0;
            if (_time.GetElapsedTime(_windowStart) >= window)
            {
                _windowStart = _time.GetTimestamp();
                _count = 0;
                suppressedInPreviousWindow = _suppressed;
                _suppressed = 0;
            }
            if (_count >= maxPerWindow)
            {
                _suppressed++;
                return false;
            }
            _count++;
            return true;
        }
    }
}
