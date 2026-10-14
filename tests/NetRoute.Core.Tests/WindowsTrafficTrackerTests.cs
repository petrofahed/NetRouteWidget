using System.Net;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class WindowsTrafficTrackerTests
{
    const int TcpSend = 10, TcpRecv = 11, TcpConnect = 12;
    static IPAddress Ip(string text) => IPAddress.Parse(text);

    static readonly IPAddress Phone4 = Ip("192.168.42.11");
    static readonly IPAddress Lan4 = Ip("192.168.86.42");
    static readonly IPAddress Phone6 = Ip("2001:db8:1::2");
    static readonly IPAddress Internet = Ip("1.1.1.1");

    static WindowsTrafficTracker Create()
    {
        var names = new ProcessNameCache(pid => pid switch { 7 or 8 => "chrome.exe", 9 => "steam.exe", _ => null });
        var tracker = new WindowsTrafficTracker(names, addressesOf: name => name == TestAdapters.Phone().Name ? [Phone6] : []);
        tracker.SetAdapters(TestAdapters.Phone(), TestAdapters.Lan());
        return tracker;
    }

    [Fact]
    public void Sent_and_received_bytes_are_upload_and_download_per_application_and_exit()
    {
        var tracker = Create();

        tracker.Record(TcpSend, Phone4, Internet, 100, pid: 7);
        tracker.Record(TcpRecv, Internet, Phone4, 900, pid: 7);
        tracker.Record(TcpRecv, Internet, Lan4, 50, pid: 9);

        var drained = tracker.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Contains(new MeasuredTraffic("chrome.exe", RouteExit.Phone, new Traffic(100, 900)), drained);
        Assert.Contains(new MeasuredTraffic("steam.exe", RouteExit.Lan, new Traffic(0, 50)), drained);
    }

    [Fact]
    public void Two_processes_of_one_application_are_merged()
    {
        var tracker = Create();

        tracker.Record(TcpRecv, Internet, Phone4, 300, pid: 7);
        tracker.Record(TcpRecv, Internet, Phone4, 200, pid: 8);

        Assert.Equal([new MeasuredTraffic("chrome.exe", RouteExit.Phone, new Traffic(0, 500))], tracker.Drain());
    }

    [Fact]
    public void A_process_that_cannot_be_named_is_reported_without_a_name()
    {
        var tracker = Create();

        tracker.Record(TcpSend, Phone4, Internet, 10, pid: 1234);

        Assert.Equal([new MeasuredTraffic(null, RouteExit.Phone, new Traffic(10, 0))], tracker.Drain());
    }

    [Fact]
    public void Drain_hands_over_everything_once()
    {
        var tracker = Create();
        tracker.Record(TcpSend, Phone4, Internet, 10, pid: 7);

        Assert.Single(tracker.Drain());
        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public void An_ipv6_address_of_the_phone_adapter_counts_for_the_phone()
    {
        var tracker = Create();

        tracker.Record(TcpRecv, Ip("2606:4700:4700::1111"), Phone6, 77, pid: 7);

        Assert.Equal([new MeasuredTraffic("chrome.exe", RouteExit.Phone, new Traffic(0, 77))], tracker.Drain());
    }

    [Fact]
    public void Events_that_are_not_data_traffic_to_local_addresses_other_adapters_and_empty_packets_are_ignored()
    {
        var tracker = Create();

        tracker.Record(TcpConnect, Phone4, Internet, 100, pid: 7);                // not a data event
        tracker.Record(TcpSend, Phone4, Ip("192.168.1.1"), 100, pid: 7);          // the home network
        tracker.Record(TcpSend, Ip("10.8.0.2"), Internet, 100, pid: 7);           // another adapter
        tracker.Record(TcpSend, Phone4, Internet, 0, pid: 7);                     // nothing moved

        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public void Nothing_counts_until_the_adapters_are_known_and_it_follows_a_replug()
    {
        var tracker = new WindowsTrafficTracker(new ProcessNameCache(_ => "chrome.exe"), addressesOf: _ => []);
        tracker.Record(TcpSend, Phone4, Internet, 10, pid: 7);
        Assert.Empty(tracker.Drain());

        tracker.SetAdapters(TestAdapters.Phone(), null);
        tracker.Record(TcpSend, Phone4, Internet, 10, pid: 7);
        Assert.Single(tracker.Drain());

        tracker.SetAdapters(TestAdapters.Phone() with { IPv4 = "192.168.42.99" }, null); // a new lease
        tracker.Record(TcpSend, Phone4, Internet, 10, pid: 7);
        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public void Observe_reads_raw_payload_values_of_any_numeric_width()
    {
        var tracker = Create();

        tracker.Observe(TcpRecv, Internet, Phone4, 900u, 7);                          // size as uint, pid as int
        tracker.Observe(TcpSend, 0x0B2AA8C0u, 0x01010101u, (ushort)100, 7u);         // addresses as little-endian numbers

        Assert.Equal([new MeasuredTraffic("chrome.exe", RouteExit.Phone, new Traffic(100, 900))], tracker.Drain());
    }

    [Fact]
    public void A_stream_of_data_events_that_cannot_be_read_raises_a_problem_and_a_readable_event_clears_it()
    {
        var tracker = Create();

        for (var i = 0; i < WindowsTrafficTracker.UnreadableEventLimit; i++) tracker.Observe(TcpRecv, "garbage", null, "x", null);
        Assert.NotNull(tracker.Problem);

        tracker.Observe(TcpRecv, Internet, Phone4, 10, 7);
        Assert.Null(tracker.Problem);
    }

    [Fact]
    public void A_few_unreadable_events_are_not_a_problem()
    {
        var tracker = Create();

        for (var i = 0; i < WindowsTrafficTracker.UnreadableEventLimit - 1; i++) tracker.Observe(TcpRecv, "garbage", null, "x", null);

        Assert.Null(tracker.Problem);
    }

    [Fact]
    public void Events_that_are_not_data_are_never_counted_as_unreadable()
    {
        var tracker = Create();

        for (var i = 0; i < WindowsTrafficTracker.UnreadableEventLimit * 2; i++) tracker.Observe(TcpConnect, null, null, null, null);

        Assert.Null(tracker.Problem);
    }

    [Fact]
    public void A_failure_inside_the_trace_callback_is_kept_as_the_problem_instead_of_escaping()
    {
        var tracker = Create();

        tracker.Fail("kernel network event could not be read: boom");

        Assert.Equal("kernel network event could not be read: boom", tracker.Problem);
    }

    [Fact]
    public void A_trace_that_ended_waits_before_starting_again_and_says_why()
    {
        var tracker = Create();

        tracker.TraceEnded(session: null, error: "session stopped by another tool");

        Assert.False(tracker.IsRunning);
        Assert.Equal("session stopped by another tool", tracker.EnsureRunning()); // inside the retry window: no new session
        Assert.Equal("session stopped by another tool", tracker.EnsureRunning());
    }

    [Fact]
    public void A_disposed_tracker_does_not_start()
    {
        var tracker = Create();

        tracker.Dispose();

        Assert.False(tracker.IsRunning);
        Assert.Equal("stopped", tracker.EnsureRunning());
    }
}
