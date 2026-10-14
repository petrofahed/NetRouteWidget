using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class KernelNetworkEventsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(26)]
    [InlineData(42)]
    [InlineData(58)]
    public void Send_events_are_data_and_upload(int id)
    {
        Assert.True(KernelNetworkEvents.IsData(id));
        Assert.True(KernelNetworkEvents.IsSend(id));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(27)]
    [InlineData(43)]
    [InlineData(59)]
    public void Receive_events_are_data_and_download(int id)
    {
        Assert.True(KernelNetworkEvents.IsData(id));
        Assert.False(KernelNetworkEvents.IsSend(id));
    }

    [Theory]
    [InlineData(12)] // connect attempt
    [InlineData(13)] // disconnect
    [InlineData(14)] // retransmit: not new data
    [InlineData(0)]
    public void Other_events_are_not_data(int id)
    {
        Assert.False(KernelNetworkEvents.IsData(id));
    }
}
