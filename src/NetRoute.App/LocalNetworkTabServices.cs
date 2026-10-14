using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Everything the Local network tab needs from the app, so the control itself knows nothing about controllers or monitors.
// Present: the view for the selected device key (null = none); it also keeps the monitor on the current adapters and devices.
// Scan: scans the LAN adapter's subnet; progress reports the number of addresses done.
// Start: starts the monitor the first time the tab is shown.
public sealed record LocalNetworkTabServices(
    Func<string?, LocalNetworkView> Present,
    Func<IReadOnlyList<LocalDevice>> GetDevices,
    Action<IReadOnlyList<LocalDevice>> SaveDevices,
    Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> Scan,
    Action Start);
