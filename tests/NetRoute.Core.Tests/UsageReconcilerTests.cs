namespace NetRoute.Core.Tests;

public class UsageReconcilerTests
{
    [Fact]
    public void Nothing_missed_and_no_overhead_is_all_zero()
    {
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(1000, 1000, 1000, 1000, phoneIsDefault: true));
    }

    [Fact]
    public void Framing_overhead_on_the_adapter_lands_in_the_phone_share()
    {
        // 1000 payload bytes seen on the phone; Windows counted 1040 (headers, handshakes). Nothing was missed.
        Assert.Equal(new UnseenSplit(40, 0), UsageReconciler.Split(1000, 1000, 1000, 1040, phoneIsDefault: true));
    }

    [Fact]
    public void Everything_missed_on_the_phone_goes_to_the_phone()
    {
        Assert.Equal(new UnseenSplit(2000, 0), UsageReconciler.Split(2000, 0, 0, 2000, phoneIsDefault: true));
    }

    [Fact]
    public void The_rest_of_the_missed_bytes_is_the_lan_share()
    {
        // 1500 missed bytes; the phone adapter grew by 1000 beyond what the open connections explain.
        Assert.Equal(new UnseenSplit(1000, 500), UsageReconciler.Split(1900, 400, 300, 1300, phoneIsDefault: true));
    }

    [Fact]
    public void An_adapter_delta_smaller_than_the_attributed_phone_bytes_means_no_missed_phone_bytes()
    {
        Assert.Equal(new UnseenSplit(0, 600), UsageReconciler.Split(1000, 400, 300, 100, phoneIsDefault: true));
    }

    [Theory]
    [InlineData(true, 3000, 0)]
    [InlineData(false, 0, 3000)]
    public void Without_the_adapter_counter_the_missed_bytes_follow_the_default_exit(bool phoneIsDefault, long phone, long lan)
    {
        Assert.Equal(new UnseenSplit(phone, lan), UsageReconciler.Split(4000, 1000, 1000, null, phoneIsDefault));
    }

    [Fact]
    public void Totals_that_are_smaller_than_what_was_seen_never_go_negative()
    {
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(100, 500, 500, null, phoneIsDefault: true));
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(-5, 0, 0, null, phoneIsDefault: false));
    }
}
