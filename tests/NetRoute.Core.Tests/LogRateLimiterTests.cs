using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class LogRateLimiterTests
{
    readonly FakeTimeProvider _time = new();

    LogRateLimiter Create() => new(maxPerWindow: 20, TimeSpan.FromMinutes(1), _time);

    [Fact]
    public void First_twenty_pass_and_the_twenty_first_is_dropped()
    {
        var limiter = Create();
        for (var i = 0; i < 20; i++) Assert.True(limiter.TryAllow(out var suppressed), $"line {i + 1}");
        Assert.False(limiter.TryAllow(out _));
    }

    [Fact]
    public void Rollover_reports_how_many_lines_were_dropped()
    {
        var limiter = Create();
        for (var i = 0; i < 25; i++) limiter.TryAllow(out _); // 20 pass, 5 dropped
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAllow(out var suppressed));
        Assert.Equal(5, suppressed);
    }

    [Fact]
    public void New_window_allows_lines_again_and_reports_nothing_when_nothing_was_dropped()
    {
        var limiter = Create();
        for (var i = 0; i < 20; i++) limiter.TryAllow(out _);
        _time.Advance(TimeSpan.FromMinutes(1));

        for (var i = 0; i < 20; i++)
        {
            Assert.True(limiter.TryAllow(out var suppressed));
            Assert.Equal(0, suppressed);
        }
        Assert.False(limiter.TryAllow(out _));
    }

    [Fact]
    public void Window_does_not_roll_over_early()
    {
        var limiter = Create();
        for (var i = 0; i < 21; i++) limiter.TryAllow(out _);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAllow(out _));
    }

    [Fact]
    public void Dropped_count_is_reported_once()
    {
        var limiter = Create();
        for (var i = 0; i < 22; i++) limiter.TryAllow(out _);
        _time.Advance(TimeSpan.FromMinutes(1));
        limiter.TryAllow(out var first);
        limiter.TryAllow(out var second);
        Assert.Equal(2, first);
        Assert.Equal(0, second);
    }
}
