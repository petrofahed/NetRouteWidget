using NetRoute.Core.Windows;
using Xunit.Abstractions;

namespace NetRoute.Core.Tests;

/// Read-only check against the real machine's Wi-Fi adapter. Never changes any setting.
[Trait("Category", "Integration")]
public class WindowsNetworkNamesIntegrationTests
{
    readonly ITestOutputHelper _output;

    public WindowsNetworkNamesIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Wifi_adapter_network_name_matches_the_real_connected_network()
    {
        var adapter = new WindowsAdapterSource().GetAdapters()
            .First(a => a.Kind == AdapterKind.Wireless && a.IsUp && a.IPv4 is not null);
        _output.WriteLine($"Using interface {adapter.Index} ({adapter.Name}), IPv4 {adapter.IPv4}");

        var name = new WindowsNetworkNames().NameOf(adapter);
        _output.WriteLine($"MSFT_NetConnectionProfile Name: {name ?? "<null>"}");

        Assert.Equal("pzhome", name);
    }
}
