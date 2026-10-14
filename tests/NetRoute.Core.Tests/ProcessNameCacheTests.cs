using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class ProcessNameCacheTests
{
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
    readonly List<int> _asked = new();

    ProcessNameCache Create(Func<int, string?>? resolve = null) =>
        new(resolve ?? (pid => { _asked.Add(pid); return pid == 7 ? "chrome.exe" : null; }), _time);

    [Fact]
    public void A_name_is_resolved_once_and_then_remembered()
    {
        var cache = Create();

        Assert.Equal("chrome.exe", cache.NameOf(7));
        Assert.Equal("chrome.exe", cache.NameOf(7));

        Assert.Equal([7], _asked);
    }

    [Fact]
    public void An_unknown_process_is_remembered_as_unknown()
    {
        var cache = Create();

        Assert.Null(cache.NameOf(9));
        Assert.Null(cache.NameOf(9));

        Assert.Equal([9], _asked);
    }

    [Fact]
    public void A_name_is_looked_up_again_after_it_expires_because_ids_get_reused()
    {
        var names = new Queue<string?>(["a.exe", "b.exe"]);
        var cache = Create(_ => names.Dequeue());

        Assert.Equal("a.exe", cache.NameOf(5));
        _time.Advance(ProcessNameCache.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal("b.exe", cache.NameOf(5));
    }

    [Fact]
    public void A_resolver_that_throws_gives_unknown_instead_of_breaking_the_trace()
    {
        var cache = Create(_ => throw new InvalidOperationException("access denied"));

        Assert.Null(cache.NameOf(4));
    }
}
