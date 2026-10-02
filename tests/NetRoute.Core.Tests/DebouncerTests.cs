using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class DebouncerTests
{
    readonly FakeTimeProvider _time = new();
    int _calls;

    Debouncer Create() => new(TimeSpan.FromMilliseconds(1500), () => _calls++, _time);

    [Fact]
    public void Burst_of_signals_fires_once_after_quiet_period()
    {
        using var debouncer = Create();
        for (var i = 0; i < 5; i++)
        {
            debouncer.Signal();
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }
        Assert.Equal(0, _calls);

        _time.Advance(TimeSpan.FromMilliseconds(1300));
        Assert.Equal(1, _calls);

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void Each_new_signal_restarts_the_quiet_period()
    {
        using var debouncer = Create();
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1400));
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.Equal(0, _calls);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void Separate_bursts_fire_separately()
    {
        using var debouncer = Create();
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(2, _calls);
    }

    [Fact]
    public void Without_signals_nothing_fires()
    {
        using var debouncer = Create();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _calls);
    }

    [Fact]
    public void Disposed_debouncer_does_not_fire()
    {
        var debouncer = Create();
        debouncer.Signal();
        debouncer.Dispose();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(0, _calls);
    }
}
