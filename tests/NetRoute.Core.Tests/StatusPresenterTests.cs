namespace NetRoute.Core.Tests;

public class StatusPresenterTests
{
    static NetworkStatus Status(
        RoutingMode mode = RoutingMode.Phone, InternetPath path = InternetPath.Phone,
        bool phone = true, bool lan = true, int? phoneMs = 38, int? lanMs = 12,
        string? error = null, InternetPath ipv6 = InternetPath.None,
        DetectionIssue missingLanIssue = DetectionIssue.NotFound) =>
        new(mode,
            new DetectionResult(
                phone ? TestAdapters.Phone() : null, phone ? DetectionIssue.None : DetectionIssue.NotFound,
                lan ? TestAdapters.Lan() : null, lan ? DetectionIssue.None : missingLanIssue),
            path, ipv6, phone ? phoneMs : null, lan ? lanMs : null, CanModify: true, error);

    [Fact]
    public void Normal_phone_mode()
    {
        var view = StatusPresenter.Present(Status());

        Assert.Equal("Internet via PHONE", view.Header);
        Assert.False(view.HeaderWarning);
        Assert.Null(view.Note);
        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "38 ms", Dot.Green, true), view.Phone);
        Assert.Equal(new AdapterRow("LAN", "192.168.86.42", "12 ms", Dot.Green, false), view.Lan);
        Assert.Equal("Internet: Phone (38 ms) · LAN ready", view.Tooltip);
        Assert.Equal(TrayColor.Green, view.TrayColor);
        Assert.False(view.TrayBadge);
    }

    [Fact]
    public void Phone_unplugged_in_phone_mode_shows_fallback()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan, phone: false));

        Assert.Equal("Internet via LAN (phone offline)", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal(new AdapterRow("Phone", "not connected", "", Dot.Gray, false), view.Phone);
        Assert.True(view.Lan.IsActive);
        Assert.Equal("Internet: LAN (12 ms) · Phone offline", view.Tooltip);
    }

    [Fact]
    public void Phone_connected_but_not_routing_shows_reason()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan));

        Assert.Equal("Internet via LAN (phone not routing)", view.Header);
    }

    [Fact]
    public void Lan_mode_fallback_to_phone()
    {
        var view = StatusPresenter.Present(Status(mode: RoutingMode.Lan, path: InternetPath.Phone, lan: false));

        Assert.Equal("Internet via PHONE (LAN offline)", view.Header);
        Assert.Equal(TrayColor.Blue, view.TrayColor);
        Assert.True(view.TrayBadge);
    }

    [Fact]
    public void Adapter_without_probe_reply_is_amber()
    {
        var view = StatusPresenter.Present(Status(phoneMs: null));

        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "no reply", Dot.Amber, true), view.Phone);
    }

    [Fact]
    public void Ambiguous_adapter_asks_to_choose()
    {
        var view = StatusPresenter.Present(Status(lan: false, missingLanIssue: DetectionIssue.Ambiguous));

        Assert.Equal("choose adapter…", view.Lan.Address);
        Assert.Equal(Dot.Gray, view.Lan.Dot);
    }

    [Fact]
    public void No_internet_is_a_warning()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.None));

        Assert.Equal("No internet", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal("Internet: none · LAN ready", view.Tooltip);
    }

    [Fact]
    public void Error_replaces_header_and_tooltip()
    {
        var view = StatusPresenter.Present(Status(error: "Ethernet IPv4: Access is denied."));

        Assert.Equal("Ethernet IPv4: Access is denied.", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.Equal("NetRoute: Ethernet IPv4: Access is denied.", view.Tooltip);
    }

    [Fact]
    public void Ipv6_on_a_different_adapter_adds_a_note()
    {
        Assert.Equal("IPv6 traffic goes via LAN", StatusPresenter.Present(Status(ipv6: InternetPath.Lan)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.Phone)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.None)).Note);
    }

    [Fact]
    public void Auto_mode_is_gray_and_other_adapter_is_named()
    {
        var auto = StatusPresenter.Present(Status(mode: RoutingMode.Auto, path: InternetPath.Lan));
        Assert.Equal(TrayColor.Gray, auto.TrayColor);
        Assert.False(auto.TrayBadge);
        Assert.Equal("Internet via LAN", auto.Header);

        Assert.Equal("Internet via other adapter", StatusPresenter.Present(Status(path: InternetPath.Other)).Header);
    }

    [Fact]
    public void Toast_when_phone_disconnects()
    {
        Assert.Equal("Phone disconnected — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan, phone: false)));
    }

    [Fact]
    public void Toast_when_phone_comes_back()
    {
        Assert.Equal("Phone back — internet via Phone",
            StatusPresenter.ToastFor(Status(path: InternetPath.Lan, phone: false), Status()));
    }

    [Fact]
    public void Toast_when_phone_stays_connected_but_loses_the_route()
    {
        Assert.Equal("Phone lost internet — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan)));
    }

    [Fact]
    public void No_toast_when_path_is_unchanged()
    {
        Assert.Null(StatusPresenter.ToastFor(Status(), Status(phoneMs: 99)));
    }

    [Fact]
    public void Toast_when_internet_is_lost()
    {
        Assert.Equal("No internet connection", StatusPresenter.ToastFor(Status(), Status(path: InternetPath.None)));
    }

    [Fact]
    public void ResolvePath_maps_best_interface_to_adapter()
    {
        var adapters = Status().Adapters;

        Assert.Equal(InternetPath.Phone, NetworkStatus.ResolvePath(31, adapters));
        Assert.Equal(InternetPath.Lan, NetworkStatus.ResolvePath(10, adapters));
        Assert.Equal(InternetPath.Other, NetworkStatus.ResolvePath(40, adapters));
        Assert.Equal(InternetPath.None, NetworkStatus.ResolvePath(null, adapters));
    }
}
