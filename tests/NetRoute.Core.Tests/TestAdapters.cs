namespace NetRoute.Core.Tests;

/// Adapter fixtures modelled on a real dual-homed PC. MACs are made up (public repo).
static class TestAdapters
{
    public static AdapterInfo Phone(int index = 31, string description = "SAMSUNG Mobile USB Remote NDIS Network Device") =>
        new(index, $"Ethernet {index}", description, AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.42.11", "02-00-00-00-00-31");

    public static AdapterInfo Lan(int index = 10, string mac = "AA-BB-CC-00-00-10") =>
        new(index, "Ethernet", "Realtek Gaming 2.5GbE Family Controller", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.86.42", mac);

    public static AdapterInfo Wifi() =>
        new(18, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", AdapterKind.Wireless, IsUp: true, HasGateway: true, "192.168.1.20", "AA-BB-CC-00-00-18");

    public static AdapterInfo VmNet(int index) =>
        new(index, $"VMware Network Adapter VMnet{index}", "VMware Virtual Ethernet Adapter for VMnet", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.80.1", $"00-50-56-C0-00-{index:D2}");

    public static AdapterInfo HyperV() =>
        new(34, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "172.26.16.1", "00-15-5D-00-00-34");

    public static AdapterInfo NordLynx() =>
        new(40, "NordLynx", "NordLynx Tunnel", AdapterKind.Other, IsUp: true, HasGateway: true, "10.5.0.2", "");
}
