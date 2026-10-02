namespace NetRoute.Core;

/// Collapses bursts of signals (e.g. a phone being plugged in fires many network events)
/// into one action, run `quiet` after the last signal.
public sealed class Debouncer : IDisposable
{
    readonly TimeSpan _quiet;
    readonly ITimer _timer;

    public Debouncer(TimeSpan quiet, Action action, TimeProvider? time = null)
    {
        _quiet = quiet;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => action(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal() => _timer.Change(_quiet, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();
}
