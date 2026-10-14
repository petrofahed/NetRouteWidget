namespace NetRoute.Core.Tests;

public class UsageBannerTests
{
    [Fact]
    public void While_smart_routing_records_there_is_no_banner()
    {
        Assert.Null(UsageBanner.Text(smartRecording: true, windowsTracking: false));
        Assert.Null(UsageBanner.Text(smartRecording: true, windowsTracking: true));
    }

    [Fact]
    public void With_smart_off_and_windows_tracing_the_banner_says_usage_is_counted_per_application()
    {
        Assert.Equal("Smart routing is off: usage is counted per application by Windows (no website rows).",
            UsageBanner.Text(smartRecording: false, windowsTracking: true));
    }

    [Fact]
    public void With_neither_the_old_banner_says_usage_is_only_recorded_while_smart_runs()
    {
        Assert.Equal("Usage is recorded only while Smart routing is running. History stays visible.",
            UsageBanner.Text(smartRecording: false, windowsTracking: false));
    }
}
